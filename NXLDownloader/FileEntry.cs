using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MapleStory_Auto_Patcher
{
    public class FileEntry
    {
        public static Action<string> Log = s => { };

        [JsonProperty(PropertyName = "objects")]
        public List<string> ChunkHashes;

        [JsonProperty(PropertyName = "objects_fsize")]
        public List<string> ChunkSizesStrings;

        [JsonProperty(PropertyName = "fsize")] public long FileSize;

        [JsonProperty(PropertyName = "mtime")] public int ModifiedTime;

        public int[] ChunkSizes => ChunkSizesStrings.Select(int.Parse).ToArray();

        public static async Task<Tuple<long, byte[]>> DownloadChunk(string productId, string chunkHash,
            long expectedSize, long position)
        {
            var chunkPath =
                $"https://download2.nexon.net/Game/nxl/games/{productId}/{productId}/{chunkHash.Substring(0, 2)}/{chunkHash}";

            var wrongData = false;
            var retry = 0;
            do
            {
                var data = new byte[0];
                try
                {
                    data = await Program.Client.GetByteArrayAsync(chunkPath);
                    var decompressedData = Program.Decompress(data);
                    if (decompressedData.Length != expectedSize && expectedSize != data.Length)
                    {
                        Log("Decompressed and chunk length doesn't match expected size");
                        continue;
                    }

                    var sha1 = SHA1.Create();
                    var sha1Hash = string.Join("", sha1.ComputeHash(decompressedData).Select(c => c.ToString("x2")));
                    if (!sha1Hash.Equals(chunkHash, StringComparison.CurrentCultureIgnoreCase))
                    {
                        Log($"Hash mismatch, expected {chunkHash}, got {sha1Hash}");
                        if (retry <= 5)
                            continue;
                        throw new InvalidDataException($"Hash does not match expected {chunkHash} got {sha1Hash}");
                    }

                    return new Tuple<long, byte[]>(position, decompressedData);
                }
                catch (Exception)
                {
                    Log(
                        $"Error decompressing chunk {chunkHash} from {chunkPath} ({data.Length} vs {expectedSize}), trying again.");
                    if (retry >= 5) throw;
                }
            } while (retry++ < 5);


            return null;
        }
    }
}