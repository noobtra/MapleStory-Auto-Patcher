using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace MapleStory_Auto_Patcher
{
    public class Manifest
    {
        public decimal BuildTime;

        [JsonProperty(PropertyName = "filepath_encoding")]
        public string FilePathEncodingName;

        public Dictionary<string, FileEntry> Files;
        public string Platform;
        public string Product;

        [JsonProperty(PropertyName = "total_compressed_size")]
        public long TotalCompressedSize;

        [JsonProperty(PropertyName = "total_objects")]
        public long TotalObjects;

        [JsonProperty(PropertyName = "total_uncompressed_size")]
        public long TotalUncompressedSize;

        public string Version;

        public DateTime BuiltAt => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((double) BuildTime)
            .ToLocalTime();

        public Encoding FilePathEncoding => FilePathEncodingName.Equals("utf16", StringComparison.CurrentCultureIgnoreCase) ? Encoding.Unicode : Encoding.ASCII;

        public Dictionary<string, FileEntry> RealFileNames =>
            Files.ToDictionary(
                c => FilePathEncoding.GetString(Convert.FromBase64CharArray(c.Key.ToCharArray(), 0, c.Key.Length))
                    .TrimStart((char) 65279),
                c => c.Value
            );

        public static Manifest Parse(byte[] data)
        {
            using var result = new MemoryStream(Program.Decompress(data));
            using var reader = new StreamReader(result, Encoding.UTF8);
            var uncompressed = reader.ReadToEnd();
            return JsonConvert.DeserializeObject<Manifest>(uncompressed);
        }
    }
}