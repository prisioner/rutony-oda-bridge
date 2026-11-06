using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;

namespace RutonyChat
{
    public class Script
    {
        private object runtime;
        public void InitParams(Dictionary<string, string> param)
        {
            string package = ResolvePackageDirectory(param);
            string data;
            if (!param.TryGetValue("DataDirectory", out data) || string.IsNullOrWhiteSpace(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RutonyOda");
            string portText; int port = 18766;
            if (param.TryGetValue("Port", out portText) && !int.TryParse(portText, out port)) throw new ArgumentException("ODA: порт должен быть числом.");
            try
            {
                var library = Path.Combine(package, "RutonyOda.dll");
                // Do not reuse a loaded/manual assembly when a registered item is incomplete.
                if (!File.Exists(library)) throw new FileNotFoundException("Missing package library.", library);
                var assembly = Assembly.LoadFrom(library);
                var type = assembly.GetType("RutonyOda.RuntimeHost", true);
                runtime = type.GetMethod("Start").Invoke(null, new object[] { package, data, port, new Action<string, string, string, string, string>(Publish) });
                string controlUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/control";
                RutonyBot.SayToWindow(WebUtility.HtmlEncode("Мост ODA включён. ")
                    + "<a href=\"" + WebUtility.HtmlEncode(controlUrl) + "\" target=\"_blank\" rel=\"noopener noreferrer\">Открыть панель ODA</a>");
            }
            catch
            {
                Closing();
                RutonyBot.SayToWindow("ODA: не удалось запустить мост. Проверьте пакет, каталог данных и свободный порт. После обновления DLL перезапустите RutonyChat.");
                throw;
            }
        }
        private void Publish(string name, string amount, string currency, string text, string id)
        {
            var item = new CurrencyControl.CurrencyItem { CharCode = currency };
            float sum = float.Parse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            ChatEventServer.Donate(ProgramProps.SiteEnum.other, name, new CurrencyControl.SumClass(sum, item), text, false, "", id);
        }
        private static string ResolvePackageDirectory(Dictionary<string, string> parameters)
        {
            string explicitDirectory;
            if (parameters.TryGetValue("PackageDirectory", out explicitDirectory) && !string.IsNullOrWhiteSpace(explicitDirectory))
                return explicitDirectory;
            string workshopDirectory = null;
            try { workshopDirectory = RutonyBotFunctions.GetScriptDirectory("rutony_oda.cs"); }
            catch { /* SDK lookup unavailable: preserve the manual installation fallback. */ }
            if (!string.IsNullOrWhiteSpace(workshopDirectory))
                return Path.Combine(workshopDirectory, "RutonyOda");
            return Path.Combine(AppContext.BaseDirectory, "scripts", "RutonyOda");
        }

        public void Closing()
        {
            var old = runtime; runtime = null;
            if (old != null) ((IDisposable)old).Dispose();
        }
    }
}
