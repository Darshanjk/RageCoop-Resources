using CoopServer;

namespace WeatherTimeSync
{
    public enum Hash : ulong
    {
        SET_WEATHER_TYPE_NOW_PERSIST = 0xED712CA327900C8A,
        SET_CLOCK_TIME = 0x47C3B5848C3E45D8
    }

    public class WeatherTimeSync : ServerScript
    {
        private string _currentWeather = "CLEAR";
        private long _lastUpdate;
        private static readonly string[] weatherNames = {
            "EXTRASUNNY",
            "CLEAR",
            "CLOUDS",
            "SMOG",
            "FOGGY",
            "OVERCAST",
            "RAIN",
            "THUNDER",
            "CLEARING",
            "NEUTRAL",
            "SNOW",
            "BLIZZARD",
            "SNOWLIGHT",
            "XMAS",
            "HALLOWEEN"
        };

        public WeatherTimeSync()
        {
            API.OnTick += API_OnTick;
            API.OnPlayerConnected += API_OnPlayerConnected;
        }

        private void API_OnTick(long tick)
        {
            // Update every 5 minutes
            if (tick - _lastUpdate > 300000)
            {
                _lastUpdate = tick;

                // Take a random value of 'weatherNames' by its length
                _currentWeather = weatherNames[new Random().Next(weatherNames.Length)];

                // Now send the weather to all connected clients
                API.SendNativeCallToAll((ulong)Hash.SET_WEATHER_TYPE_NOW_PERSIST, _currentWeather);
                // Get the current server time
                DateTime timeNow = DateTime.Now;
                // Now send the current server time to all connected clients
                API.SendNativeCallToAll((ulong)Hash.SET_CLOCK_TIME, timeNow.Hour, timeNow.Minute, timeNow.Second);

                // Send all connected clients that the weather and time have been updated from the server
                API.SendChatMessageToAll("Weather and time have been updated!");
            }
        }

        // When a new player connects to the server, we send them the current weather and time
        private void API_OnPlayerConnected(Client client)
        {
            client.SendNativeCall((ulong)Hash.SET_WEATHER_TYPE_NOW_PERSIST, _currentWeather);
            DateTime timeNow = DateTime.Now;
            client.SendNativeCall((ulong)Hash.SET_CLOCK_TIME, timeNow.Hour, timeNow.Minute, timeNow.Second);

            client.SendChatMessage("Weather and time have been updated!");
        }
    }
}
