using System.Data.SQLite;
using System.ComponentModel;
using System.Xml.Serialization;
using CoopServer;
using Race.Objects;

namespace Race
{
    public class Race : ServerScript
    {
        public const ulong REQUEST_MODEL = 0x963D27A58DF860AC;
        public const ulong SET_MODEL_AS_NO_LONGER_NEEDED = 0xE532F5D78798DAAB;
        public const ulong CREATE_CHECKPOINT = 0x0134F0835AB6BFCB;
        public const ulong DELETE_CHECKPOINT = 0xF5ED37F54CD4D52E;
        public const ulong ADD_BLIP_FOR_COORD = 0x5A039BB0BCA604B6;
        public const ulong SET_BLIP_ALPHA = 0x45FF974EEE1C8734;
        public const ulong CREATE_VEHICLE = 0xAF35D0D2583051B0;
        public const ulong SET_PED_INTO_VEHICLE = 0xF75B0D629E1C063D;
        public const ulong SET_VEHICLE_FIXED = 0x115722B1B9C14C1C;
        public const ulong SET_VEHICLE_ENGINE_ON = 0x2497C4717C8B881E;
        public const ulong STOP_ENTITY_FIRE = 0x7F0DD2EBBB651AFF;
        public const ulong CREATE_OBJECT = 0x509D5878EB39E842;
        public const ulong FREEZE_ENTITY_POSITION = 0x428CA6DBD1094446;
        public const ulong SET_ENTITY_COORDS = 0x06843DA7060A026B;
        public const ulong SET_ENTITY_ROTATION = 0x8524A8B0171D5E07;
        public const ulong SET_ENTITY_HEADING = 0x8E2530AA8ADA980E;
        public const ulong SET_OBJECT_TEXTURE_VARIATION = 0x971DA0055324D033;
        public const ulong REQUEST_SCRIPT_AUDIO_BANK = 0x2F844A8B08D76685;
        public const ulong PLAY_SOUND_FRONTEND = 0x67C540AA08E4A6F5;
        public const ulong ON_ENTER_MP = 0x0888C3502DBBEEF5;
        public const ulong SET_ISLAND_HOPPER_ENABLED = 0x9A9D1BA639675CF1;

        public static List<Map> Maps;
        public static Session Session;

        private readonly XmlSerializer Serializer = new(typeof(Map));
        private static readonly Random Random = new();
        private static SQLiteConnection Connection;

        private static DiscordBot DiscordBot;

        public Race()
        {
            API.OnStart += () =>
            {
                Logging.Info("Resource started successfully!");

                Session.State = State.Voting;
                Session.Votes = new Dictionary<Client, string>();
                Session.Players = new List<Player>();
                Maps = Util.GetMaps()?.Select(map => (Map)Serializer.Deserialize(new StreamReader(map))).ToList();
                InitDB();
                DiscordBot = new DiscordBot();
            };
            API.OnStop += () =>
            {
                Logging.Info($"Resource is stopped! Clients: {API.GetAllClients().Count}");
            };
            API.OnPlayerConnected += (Client client) =>
            {
                var msg = $"{client.Player.Username} connected";
                Logging.Info(msg);
                DiscordBot.SendToDiscord(msg).GetAwaiter().GetResult();

                if (Session.State != State.Voting && Session.State != State.Preparing)
                    client.SendChatMessage("A race has already started, wait for the next round or use /join to join the race");
                else
                    client.SendChatMessage("The race will start soon, use /vote to vote for a map");

                client.SendNativeCall(REQUEST_SCRIPT_AUDIO_BANK, "HUD_MINI_GAME_SOUNDSET", true);
                client.SendNativeCall(ON_ENTER_MP, true);
            };
            API.OnPlayerDisconnected += (Client client) =>
            {
                var msg = $"{client.Player.Username} disconnected";
                Logging.Info(msg);
                DiscordBot.SendToDiscord(msg).GetAwaiter().GetResult();

                Leave(client, true);
            };
            API.OnTick += OnTick;
            API.OnChatMessage += ChatMessage;

            API.RegisterCommands<Commands>();
        }

        private static void OnTick(long tick)
        {
            if (Session.State == State.Voting && API.GetAllClients().Count >= 1)
            {
                Session.NextEvent = DateTime.Now.AddSeconds(15);
                Session.State = State.Preparing;
                Session.Votes = new Dictionary<Client, string>();
                lock (Session.Players)
                    foreach (var player in Session.Players)
                    {
                        RemoveCheckpoint(player);
                        player.RememberedBlips.Clear();
                        player.RememberedProps.Clear();
                        player.CheckpointsPassed = 0;
                    }
                API.SendChatMessageToAll("Starting in 15 seconds, use /vote to vote for a map");
            }

            if (Session.State == State.Preparing && DateTime.Now > Session.NextEvent)
            {
                Session.State = State.Starting;

                API.SendCleanUpWorldToAll();

                var map = Session.Votes.GroupBy(x => x.Value).OrderByDescending(vote => vote.Count()).FirstOrDefault()?.Key ?? Util.GetRandomMap();
                Session.Map = Maps.First(x => x.Name == map);
                API.SendChatMessageToAll("Map: " + map);
                DiscordBot.SendToDiscord("Starting race with map " + map).GetAwaiter().GetResult();
                var record = Record(map);
                if (record.Item1 > 0)
                {
                    var msg = $"Record: {TimeSpan.FromMilliseconds(record.Item1):m\\:ss\\.ff} by {record.Item2}";
                    API.SendChatMessageToAll(msg);
                    DiscordBot.SendToDiscord(msg).GetAwaiter().GetResult();
                }
                API.SendChatMessageToAll("Use /respawn to return to the last checkpoint or /leave to leave the race");

                foreach (var client in API.GetAllClients())
                    Join(client);

                var setupPlayers = new Thread((ThreadStart)delegate
                {
                    lock (Session.Players)
                        foreach (var player in Session.Players)
                        {
                            player.Client.SendNativeCall(SET_ISLAND_HOPPER_ENABLED, "HeistIsland", CayoPericoCheck());
                            player.VehicleHash = (int)Session.Map.AvailableVehicles[Random.Next(Session.Map.AvailableVehicles.Length)];
                            player.Client.SendNativeCall(REQUEST_MODEL, player.VehicleHash);
                        }
                    Thread.Sleep(1000);
                    int spawnPoint = 0;
                    lock (Session.Players)
                        foreach (var player in Session.Players)
                        {
                            CreateProps(player);
                            AddCheckpoint(player, 0);
                            CreateBlip(player, 0);
                            CreateBlip(player, 1);
                            CreateVehicle(player, spawnPoint, true);
                            spawnPoint++;
                        }
                });
                setupPlayers.Start();

                API.SendChatMessageToAll("The race is about to start, get ready");

                var countdown = new Thread((ThreadStart)delegate
                {
                    Thread.Sleep(10000);
                    for (int i = 3; i > 0; i--)
                    {
                        API.SendChatMessageToAll($"{i}");
                        Thread.Sleep(1000);
                    }
                    API.SendChatMessageToAll("Go!");

                    lock (Session.Players)
                        foreach (var player in Session.Players)
                            player.Client.SendNativeCall(FREEZE_ENTITY_POSITION, player.Vehicle, false);

                    Session.State = State.Started;
                    Session.RaceStart = Environment.TickCount64;
                });
                countdown.Start();
            }

            if (Session.State == State.Started)
            {
                lock (Session.Players)
                    foreach (var player in Session.Players)
                    {
                        if (player.Client?.Player.Position == null) continue;

                        if (VectorExtensions.Distance(player.Client.Player.Position, Session.Map.Checkpoints[player.CheckpointsPassed]) < 15f)
                        {
                            player.Client.SendNativeCall(PLAY_SOUND_FRONTEND, 0, "CHECKPOINT_NORMAL", "HUD_MINI_GAME_SOUNDSET");

                            RemoveCheckpoint(player);

                            if (player.RememberedBlips.Count > player.CheckpointsPassed)
                                player.Client.SendNativeCall(SET_BLIP_ALPHA, player.RememberedBlips[player.CheckpointsPassed], 0);

                            if (Session.Map.Checkpoints.Length > player.CheckpointsPassed + 1)
                            {
                                player.CheckpointsPassed++;
                                AddCheckpoint(player, player.CheckpointsPassed);

                                if (Session.Map.Checkpoints.Length > player.CheckpointsPassed + 1)
                                    CreateBlip(player, player.CheckpointsPassed + 1);
                            }
                            else
                            {
                                Session.State = State.Voting;

                                var time = Environment.TickCount64 - Session.RaceStart;
                                var msg = $"{player.Client.Player.Username} finished in {TimeSpan.FromMilliseconds(time):m\\:ss\\.ff}";
                                var record = Record(Session.Map.Name);
                                if (record.Item1 > 0 && time < record.Item1)
                                    msg += " (new record)";
                                if (Session.Players.Count > 1)
                                    msg += $" ({Wins(player.Client.Player.Username) + 1} wins)";
                                API.SendChatMessageToAll(msg);
                                DiscordBot.SendToDiscord(msg).GetAwaiter().GetResult();
                                SaveTime(Session.Map.Name, player.Client.Player.Username, time, Session.Players.Count > 1 ? 1 : 0);
                            }
                        }
                    }
            }
        }

        public static void AddCheckpoint(Player player, int i)
        {
            var next = Session.Map.Checkpoints[i];
            var last = Session.Map.Checkpoints.Length == i + 1;
            var pointTo = Session.Map.Checkpoints[last ? i - 1 : i + 1];
            player.Client.SendNativeResponse((object o) =>
            {
                player.RememberedCheckpoint = (int)o;
            }, CREATE_CHECKPOINT, typeof(int), last ? 4 : 0, next.X, next.Y, next.Z, pointTo.X, pointTo.Y, pointTo.Z, 10f, 241, 247, 57, 180, 0);
        }

        public static void RemoveCheckpoint(Player player)
        {
            player.Client.SendNativeCall(DELETE_CHECKPOINT, player.RememberedCheckpoint);
        }

        public static void CreateVehicle(Player player, int spawnPoint, bool freeze)
        {
            var position = Session.Map.SpawnPoints[spawnPoint % Session.Map.SpawnPoints.Length].Position;
            var heading = Session.Map.SpawnPoints[spawnPoint % Session.Map.SpawnPoints.Length].Heading;
            player.Client.SendNativeCall(SET_ENTITY_COORDS, player.Client.Player.PedHandle, position.X + 4f, position.Y, position.Z, 1, 0, 0, 1);
            player.Client.SendNativeResponse((object o) =>
                {
                    player.Client.SendNativeCall(SET_PED_INTO_VEHICLE, player.Client.Player.PedHandle, (int)o, -1);
                    if (freeze)
                        player.Client.SendNativeCall(FREEZE_ENTITY_POSITION, (int)o, true);
                    player.Client.SendNativeCall(SET_MODEL_AS_NO_LONGER_NEEDED, player.VehicleHash);
                    player.Vehicle = (int)o;
                }, CREATE_VEHICLE, typeof(int), player.VehicleHash, position.X, position.Y, position.Z, heading, false, false);
        }

        public static void CreateBlip(Player player, int i)
        {
            var pos = Session.Map.Checkpoints[i];
            player.Client.SendNativeResponse((object o) =>
                {
                    player.RememberedBlips.Add((int)o);
                }, ADD_BLIP_FOR_COORD, typeof(int), pos.X, pos.Y, pos.Z);
        }

        public static void CreateProps(Player player)
        {
            foreach (var prop in Session.Map.DecorativeProps)
            {
                player.Client.SendNativeCall(REQUEST_MODEL, prop.Hash);
                player.Client.SendNativeResponse((object o) =>
                    {
                        player.Client.SendNativeCall(SET_ENTITY_COORDS, (int)o,
                            prop.Position.X, prop.Position.Y, prop.Position.Z, 0, 0, 0, 1);
                        player.Client.SendNativeCall(SET_ENTITY_ROTATION, (int)o,
                            prop.Rotation.X, prop.Rotation.Y, prop.Rotation.Z, 2, 1);
                        if (prop.Dynamic)
                            player.Client.SendNativeCall(FREEZE_ENTITY_POSITION, (int)o, true);
                        if (prop.Texture > 0 && prop.Texture < 16)
                            player.Client.SendNativeCall(SET_OBJECT_TEXTURE_VARIATION, (int)o, prop.Texture);
                        player.Client.SendNativeCall(SET_MODEL_AS_NO_LONGER_NEEDED, prop.Hash);
                        player.RememberedProps.Add((int)o);
                    }, CREATE_OBJECT, typeof(int), prop.Hash, prop.Position.X, prop.Position.Y, prop.Position.Z, 1, 1, prop.Dynamic);
            }
        }

        public static void Join(Client client)
        {
            lock (Session.Players)
                if (Session.Players.Any(x => x.Client == client))
                    return;

            if (Session.State == State.Started)
            {
                client.SendNativeCall(SET_ISLAND_HOPPER_ENABLED, "HeistIsland", CayoPericoCheck());

                var setupPlayer = new Thread((ThreadStart)delegate
                {
                    var player = new Player(client)
                    {
                        VehicleHash = (int)Session.Map.AvailableVehicles[Random.Next(Session.Map.AvailableVehicles.Length)]
                    };
                    client.SendNativeCall(REQUEST_MODEL, player.VehicleHash);
                    Thread.Sleep(1000);
                    CreateProps(player);
                    AddCheckpoint(player, 0);
                    CreateBlip(player, 0);
                    CreateBlip(player, 1);
                    CreateVehicle(player, Random.Next(Session.Map.SpawnPoints.Length), false);
                    lock (Session.Players)
                        Session.Players.Add(player);
                    API.SendChatMessageToAll($"{client.Player.Username} joined the race");
                });
                setupPlayer.Start();
            }
            else
                lock (Session.Players)
                    Session.Players.Add(new Player(client));
        }

        public static void Leave(Client client, bool disconnected)
        {
            if (Session.Votes.ContainsKey(client))
                Session.Votes.Remove(client);

            Player player;
            lock (Session.Players)
                player = Session.Players.FirstOrDefault(x => x.Client == client);
            if (player != default)
            {
                if (!disconnected)
                {
                    player.Client.SendCleanUpWorld();
                    player.RememberedBlips.Clear();
                    player.RememberedProps.Clear();
                    API.SendChatMessageToAll($"{client.Player.Username} left the race");
                }

                lock (Session.Players)
                    Session.Players.Remove(player);
            }

            lock (Session.Players)
                if (!Session.Players.Any())
                    Session.State = State.Voting;
        }

        public static void Respawn(Client client)
        {
            Player player;
            lock (Session.Players)
                player = Session.Players.FirstOrDefault(x => x.Client == client);
            if (player != default)
            {
                var last = player.CheckpointsPassed > 0 ? Session.Map.Checkpoints[player.CheckpointsPassed - 1] :
                    Session.Map.SpawnPoints[Random.Next(Session.Map.SpawnPoints.Length)].Position;
                var dir = VectorExtensions.Normalize(Session.Map.Checkpoints[player.CheckpointsPassed] - last);
                var heading = (float)(-Math.Atan2(dir.X, dir.Y) * 180.0 / Math.PI);
                player.Client.SendNativeCall(SET_ENTITY_COORDS, player.Vehicle, last.X, last.Y, last.Z, 0, 0, 0, 1);
                player.Client.SendNativeCall(SET_ENTITY_HEADING, player.Vehicle, heading);
                player.Client.SendNativeCall(SET_PED_INTO_VEHICLE, player.Client.Player.PedHandle, player.Vehicle, -1);
                player.Client.SendNativeCall(STOP_ENTITY_FIRE, player.Vehicle);
                player.Client.SendNativeCall(SET_VEHICLE_FIXED, player.Vehicle);
                player.Client.SendNativeCall(SET_VEHICLE_ENGINE_ON, player.Vehicle, true, true);
            }
        }

        private static bool CayoPericoCheck()
        {
            return VectorExtensions.Distance(Session.Map.SpawnPoints[0].Position, new LVector3(4700f, -5145f, 0f)) < 2000f;
        }

        private static void InitDB()
        {
            var filename = Path.Combine(AppContext.BaseDirectory, "resources", "times.db");

            if (!File.Exists(filename))
                SQLiteConnection.CreateFile(filename);

            var connectionString = new SQLiteConnectionStringBuilder()
            {
                DataSource = filename,
                Version = 3
            };

            Connection = new SQLiteConnection(connectionString.ToString());
            Connection.Open();

            new SQLiteCommand(@"
                CREATE TABLE IF NOT EXISTS `times` (
                    `Id` INTEGER PRIMARY KEY AUTOINCREMENT,
                    `Race` TEXT,
                    `Player` TEXT,
                    `Time` INTEGER,
                    `Win` INTEGER
                );"
            , Connection).ExecuteNonQuery();
        }

        private static void SaveTime(string race, string player, long time, int win)
        {
            var query = new SQLiteCommand(
                "INSERT INTO `times` (`Race`, `Player`, `Time`, `Win`) VALUES (@race, @player, @time, @win);", Connection);
            query.Parameters.AddWithValue("@race", race);
            query.Parameters.AddWithValue("@player", player);
            query.Parameters.AddWithValue("@time", time);
            query.Parameters.AddWithValue("@win", win);
            query.ExecuteNonQuery();
        }

        private static Tuple<long, string> Record(string race)
        {
            var query = new SQLiteCommand("SELECT * FROM `times` WHERE `race` = @race ORDER BY `time` ASC LIMIT 1;", Connection);
            query.Parameters.AddWithValue("@race", race);

            Tuple<long, string> record;
            var reader = query.ExecuteReader();
            if (reader.Read())
                record = new((long)reader["Time"], (string)reader["Player"]);
            else
                record = new(0, "");
            reader.Close();

            return record;
        }

        private static int Wins(string player)
        {
            var query = new SQLiteCommand("SELECT COUNT(*) FROM `times` WHERE `player` = @player AND `win` = 1;", Connection);
            query.Parameters.AddWithValue("@player", player);

            return Convert.ToInt32(query.ExecuteScalar());
        }

        public static void ChatMessage(string username, string message, CancelEventArgs args)
        {
            if (username != "Server")
                DiscordBot.SendToDiscord(message, username).GetAwaiter().GetResult();
        }
    }
}