using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MapleStory_Auto_Patcher.Extensions;
using SharpCompress.Compressors;
using SharpCompress.Compressors.Deflate;

namespace MapleStory_Auto_Patcher
{
    internal class Program
    {
        private static readonly ConcurrentQueue<string> _consoleMessages = new ConcurrentQueue<string>();

        public static HttpClient
            Client = new HttpClient(); // Instantiating a static http client to prevent socket exhaustion problems.

        private static readonly string[] SizeSuffixes =
            {"bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"};

        public static IConfiguration Configuration { get; set; }

        public static void Log(string message)
        {
            _consoleMessages.Enqueue(message);
        }

        private static async Task Main(string[] args)
        {
            IConfigurationBuilder configurationBuilder = new ConfigurationBuilder();
            Configuration = configurationBuilder.Add(
                (Action<WritableJsonConfigurationSource>) (s =>
                {
                    s.FileProvider = null;
                    s.Path = "AppSettings.json";
                    s.Optional = false;
                    s.ReloadOnChange = true;
                    s.ResolveFileProvider();
                })).Build();
            while (true)
            {
                var manifestUrl = await Client.GetStringAsync("http://3.129.199.50/manifest.txt");
                var manifestHash = await Client.GetStringAsync(manifestUrl);
                if (manifestHash != Configuration.GetSection("lastManifestHash").Value)
                {
                    await GetManifest(manifestHash);
                    Configuration.GetSection("lastManifestHash").Value = manifestHash;
                }

                Console.WriteLine("Checking hashes again in 10 minutes...");
                await Task.Delay(TimeSpan.FromMinutes(10));
            }
        }

        private static async Task GetManifest(string hash)
        {
            Manifest manifest = null;

            // Set the log handler for when hash mismatches or wrong sizes
            FileEntry.Log = Log;
            Console.WriteLine("Grabbing manifest hash from server...");
            Console.WriteLine("Downloading manifest");
            try
            {
                var manifestCompressed =
                    await Client.GetByteArrayAsync($"https://download2.nexon.net/Game/nxl/games/10100/{hash}");
                manifest = Manifest.Parse(manifestCompressed);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error getting manifest");
                Console.WriteLine(ex.Message);
            }

            Console.WriteLine(
                $"This download will be {SizeSuffix(manifest.TotalUncompressedSize)}. Assuming you do not have MapleStory already installed.");
            Console.WriteLine("Download starting in 3 seconds...");
            await Task.Delay(3000);
            Download(manifest);
        }

        public static void Download(Manifest manifest)
        {
            var running = true;
            // Handle the console messages in its own thread so as to prevent any locking or messages being written at the same time
            var consoleQueue = new Thread(() =>
            {
                string message = null;
                while (running || _consoleMessages.TryDequeue(out message))
                    do
                    {
                        if (message != null) Console.WriteLine(message);
                        Thread.Sleep(1);
                    } while (_consoleMessages.TryDequeue(out message));
            });

            consoleQueue.Start();

            var output = Configuration.GetValue<string>("installationDirectory");

            if (!Directory.Exists(output)) Directory.CreateDirectory(output);

            var fileNames = manifest.RealFileNames;
            // Build the directory tree before we start downloading
            var directories = fileNames
                .Where(c => c.Value.ChunkHashes.Count > 0 && c.Value.ChunkHashes.First().Equals("__DIR__")).ToArray();

            foreach (var directory in directories)
            {
                var subDirectory = Path.Combine(output, directory.Key);
                if (File.Exists(subDirectory)) File.Delete(subDirectory);
                if (!Directory.Exists(subDirectory)) Directory.CreateDirectory(subDirectory);
            }

            var toDownload = manifest.TotalUncompressedSize;
            long downloaded = 0;

            Parallel.ForEach(fileNames.Where(c => !directories.Contains(c) && c.Value.ChunkHashes.Count > 0), (file) =>
            {
                var filePath = Path.Combine(output, file.Key);
                Log($"Starting download of {file.Key}");

                long position = 0;
                long existingFileSize = 0;
                long firstChunkSize = file.Value.ChunkSizes.First();
                // Get all of the chunks in their own threads
                var writtenSize = file.Value.ChunkHashes.Select(hash =>
                {
                    Tuple<long, byte[]> chunk = null;
                    var index = file.Value.ChunkHashes.IndexOf(hash); // Which chunk are we downloading
                    long size = file.Value.ChunkSizes[index]; // How big is the chunk
                    long realSize = 0;
                    do
                    {
                        var sha1 = SHA1.Create();

                        if (!File.Exists(filePath)) File.Create(filePath).Dispose();
                        else // Otherwise check the hashes
                        {
                            using var fileOut = File.OpenRead(filePath);
                            existingFileSize = fileOut.Length;
                            Log($"Verifying existing data at {position} ({size} / {firstChunkSize})");
                            byte[] existing;
                            string sha1Hash;
                            if (fileOut.Length >= position + size)
                            {
                                existing = new byte[size];
                                fileOut.Position = position;
                                var read = 0;
                                while ((read += fileOut.Read(existing, read, (int) (size - read))) != size)
                                    ; // Usually less than int.max so this should be okay
                                sha1Hash = string.Join("",
                                    sha1.ComputeHash(existing).Select(c => c.ToString("x2")));
                                if (sha1Hash.Equals(hash))
                                {
                                    Log("Hash check passed, skipping downloading");
                                    realSize = size;
                                    break;
                                }

                                if (fileOut.Length >= position + firstChunkSize && firstChunkSize != size)
                                {
                                    existing = new byte[firstChunkSize];
                                    fileOut.Position = position;
                                    read = 0;
                                    while ((read += fileOut.Read(existing, read, (int) (firstChunkSize - read))) !=
                                           firstChunkSize) ; // Usually less than int.max so this should be okay
                                    sha1Hash = string.Join("",
                                        sha1.ComputeHash(existing).Select(c => c.ToString("x2")));
                                    if (sha1Hash.Equals(hash))
                                    {
                                        Log("Hash check passed, skipping downloading");
                                        realSize = firstChunkSize;
                                        break;
                                    }
                                }
                                else
                                {
                                    Log("Chunk didn't match hash");
                                }
                            }

                            if (file.Value.ChunkHashes.Count == 1)
                            {
                                existing = new byte[fileOut.Length];
                                var read = 0;
                                fileOut.Position = 0;
                                while ((read += fileOut.Read(existing, read, (int) (fileOut.Length - read))) !=
                                       fileOut.Length) ; // Usually less than int.max so this should be okay
                                sha1Hash = string.Join("",
                                    sha1.ComputeHash(existing).Select(c => c.ToString("x2")));
                                if (sha1Hash.Equals(hash))
                                {
                                    Log("Hash check passed, skipping downloading");
                                    realSize = fileOut.Length;
                                    break;
                                }

                                Log("File didn't match hash");

                                var compressed = Compress(existing);
                                sha1Hash = string.Join("",
                                    sha1.ComputeHash(compressed).Select(c => c.ToString("x2")));
                                if (sha1Hash.Equals(hash))
                                {
                                    Log("Hash check passed, skipping downloading");
                                    realSize = compressed.Length;
                                    break;
                                }

                                Log("Compressed file didn't match hash");
                            }
                        }

                        using (var fileOut = File.OpenWrite(filePath))
                        {
                            Log($"Downloading {hash}");
                            chunk = FileEntry.DownloadChunk(manifest.Product, hash, size, position)
                                .Result; // Download the chunk
                            realSize = chunk.Item2.Length;

                            fileOut.Position = chunk.Item1; // The chunk's offset
                            fileOut.Write(chunk.Item2, 0, chunk.Item2.Length); // Write the chunk data to the file
                            Log(
                                $"Wrote 0x{chunk.Item2.Length:X} at 0x{chunk.Item1:X} to {file.Key}");
                            fileOut.Flush(); // Flush it out and dispose of the FileStream
                        }
                    } while (chunk == null);

                    position += realSize;
                    downloaded += realSize;
                    Log(
                        $"Downloaded: {(position * 100f / file.Value.FileSize):0.00}% {position} / {file.Value.FileSize} total: {(downloaded * 100f / toDownload):0.00}% {downloaded} / {toDownload}");

                    chunk = null;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true); // Try to GC if possible

                    return realSize;
                }).Sum(); // Get the sum of the chunked data

                if (writtenSize != file.Value.FileSize)
                    Log("ERROR, mismatch written and expected size");
                if (file.Value.FileSize != existingFileSize && file.Value.FileSize != writtenSize)
                {
                    Log($"Existing file size does not match expected size, trimming to {file.Value.FileSize}");
                    using var fileOut = File.OpenWrite(filePath);
                    fileOut.SetLength(file.Value.FileSize);
                }

                Log($"{file.Key} Total: {writtenSize} Expected: {file.Value.FileSize}");
            });

            // Exit out of the console message processor
            running = false;
            consoleQueue.Join(); // Wait for console processor to exit
        }

        public static byte[] Decompress(byte[] data)
        {
            using var str = new MemoryStream(data);
            return Decompress(str);
        }

        public static byte[] Compress(byte[] data)
        {
            using var str = new MemoryStream(data);
            return Compress(str);
        }

        public static byte[] Decompress(Stream str)
        {
            using var result = new MemoryStream();
            using var inflate = new ZlibStream(str, CompressionMode.Decompress, true);
            inflate.CopyTo(result);

            result.Position = 0;
            return result.ToArray();
        }

        public static byte[] Compress(Stream str)
        {
            using var result = new MemoryStream();
            using var deflate = new ZlibStream(str, CompressionMode.Compress, CompressionLevel.Level0, true,
                Encoding.ASCII);
            deflate.CopyTo(result);

            result.Position = 0;
            return result.ToArray();
        }

        private static string SizeSuffix(long value, int decimalPlaces = 1)
        {
            if (decimalPlaces < 0) throw new ArgumentOutOfRangeException(nameof(decimalPlaces));
            if (value < 0) return "-" + SizeSuffix(-value);
            if (value == 0) return string.Format("{0:n" + decimalPlaces + "} bytes", 0);

            // mag is 0 for bytes, 1 for KB, 2, for MB, etc.
            var mag = (int) Math.Log(value, 1024);

            // 1L << (mag * 10) == 2 ^ (10 * mag) 
            // [i.e. the number of bytes in the unit corresponding to mag]
            var adjustedSize = (decimal) value / (1L << (mag * 10));

            // make adjustment when the value is large enough that
            // it would round up to 1000 or more
            if (Math.Round(adjustedSize, decimalPlaces) >= 1000)
            {
                mag += 1;
                adjustedSize /= 1024;
            }

            return string.Format("{0:n" + decimalPlaces + "} {1}",
                adjustedSize,
                SizeSuffixes[mag]);
        }
    }
}