using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Skalia.Launcher
{
    [DataContract]
    public sealed class SkaliaSettings
    {
        [DataMember] public string Language { get; set; } = "en";
        [DataMember] public string ServerName { get; set; } = "Server";
        [DataMember] public string Map { get; set; } = "PEI";
        [DataMember] public int MaxPlayers { get; set; } = 24;
        [DataMember] public string Difficulty { get; set; } = "Normal";
        [DataMember] public string Perspective { get; set; } = "Both";
        [DataMember] public bool PvP { get; set; } = true;
        [DataMember] public bool Cheats { get; set; } = true;
        [DataMember] public int Port { get; set; } = 27025;
        [DataMember] public string Password { get; set; } = "";
        [DataMember] public bool ShowServerConsole { get; set; } = false;

        [DataMember] public int BotCount { get; set; } = 4;
        [DataMember] public List<string> BotNames { get; set; } = new List<string>();
        [DataMember] public bool AutoCallBots { get; set; } = true;

        [DataMember] public Dictionary<string, string> GameRules { get; set; } = new Dictionary<string, string>();
        [DataMember] public string WelcomeMessage { get; set; } = "";

        [DataMember] public List<string> DisabledPlugins { get; set; } = new List<string>();
        [DataMember] public List<ulong> WorkshopIds { get; set; } = new List<ulong>();
        [DataMember] public bool WorkshopImported { get; set; }

        public static SkaliaSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var loaded = (SkaliaSettings)new DataContractJsonSerializer(typeof(SkaliaSettings)).ReadObject(stream);
                        if (loaded != null)
                        {
                            loaded.Normalize();
                            return loaded;
                        }
                    }
                }
            }
            catch
            {
            }
            var settings = new SkaliaSettings();
            settings.Normalize();
            return settings;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, ownsStream: false, indent: true))
                {
                    new DataContractJsonSerializer(typeof(SkaliaSettings)).WriteObject(writer, this);
                }
                File.WriteAllBytes(path, stream.ToArray());
            }
        }

        private void Normalize()
        {
            // DataContract nao roda inicializadores ao desserializar.
            if (BotNames == null) BotNames = new List<string>();
            if (DisabledPlugins == null) DisabledPlugins = new List<string>();
            if (GameRules == null) GameRules = new Dictionary<string, string>();
            if (WelcomeMessage == null) WelcomeMessage = "";
            if (Language != "pt-BR") Language = "en";
            if (WorkshopIds == null) WorkshopIds = new List<ulong>();
            if (string.IsNullOrWhiteSpace(ServerName) || ServerName == "SKALIA") ServerName = "Server";
            if (string.IsNullOrWhiteSpace(Map)) Map = "PEI";
            if (string.IsNullOrWhiteSpace(Difficulty)) Difficulty = "Normal";
            if (string.IsNullOrWhiteSpace(Perspective)) Perspective = "Both";
            if (Password == null) Password = "";
            if (Port <= 0) Port = 27025;
            if (MaxPlayers <= 0) MaxPlayers = 24;
        }
    }
}
