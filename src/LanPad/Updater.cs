using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LanPad
{
    /// <summary>
    /// Updates from the GitHub releases of Ralph313-creator/LanPad: reads the latest release, downloads its
    /// LanPad-Setup-&lt;version&gt;.exe (SHA-256 checked against the digest GitHub publishes) and runs it silently.
    /// The setup keeps lanpad.ini / games.user.ini and starts LanPad again when it is done.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "Ralph313-creator/LanPad";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const string UserAgent = "LanPad-Updater";

        public sealed class UpdateInfo
        {
            public Version Version;
            public string SetupUrl;
            /// <summary>Lower-case hex SHA-256 of the setup exe, or null when the release has no digest.</summary>
            public string Sha256;
            public long Size;
            public string PageUrl;
        }

        public static Version Current
        {
            get { return ParseVersion(Application.ProductVersion) ?? new Version(0, 0, 0); }
        }

        /// <summary>"v0.3.4" / "0.4" / "1.2.3+abc" -> Version (always 3 parts), or null.</summary>
        public static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var m = Regex.Match(tag.Trim(), @"^v?(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        public static bool IsNewer(Version latest, Version current) =>
            latest != null && current != null && latest > current;

        /// <summary>Reads a GitHub "release" JSON object. Null when it is not valid or has no setup exe.</summary>
        public static UpdateInfo ParseRelease(string json)
        {
            try
            {
                var release = new JavaScriptSerializer().DeserializeObject(json) as IDictionary<string, object>;
                if (release == null) return null;
                var version = ParseVersion(Str(release, "tag_name"));
                if (version == null) return null;
                var assets = (release.TryGetValue("assets", out object a) ? a as IEnumerable : null) ?? new object[0];
                foreach (var asset in assets.OfType<IDictionary<string, object>>())
                {
                    string name = Str(asset, "name") ?? "";
                    if (!Regex.IsMatch(name, @"^LanPad-Setup-.*\.exe$", RegexOptions.IgnoreCase)) continue;
                    string digest = Str(asset, "digest");
                    return new UpdateInfo
                    {
                        Version = version,
                        SetupUrl = Str(asset, "browser_download_url"),
                        Sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            ? digest.Substring(7).ToLowerInvariant() : null,
                        Size = asset.TryGetValue("size", out object size) && size != null ? Convert.ToInt64(size) : 0,
                        PageUrl = Str(release, "html_url") ?? ReleasesPage,
                    };
                }
                return null;
            }
            catch (Exception)
            {
                return null; // not JSON / unexpected shape
            }
        }

        static string Str(IDictionary<string, object> d, string key) =>
            d.TryGetValue(key, out object v) ? v as string : null;

        public static string FileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
        }

        static WebClient NewClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
            return wc;
        }

        /// <summary>The latest release, or null when there is none / no internet (LAN-only shops are fine).</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            try
            {
                using (var wc = NewClient())
                {
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    var info = ParseRelease(await wc.DownloadStringTaskAsync(LatestApi));
                    Log.Info(info == null ? "Update check: no release with a setup exe"
                                          : $"Update check: latest v{info.Version}, this is v{Current}");
                    return info;
                }
            }
            catch (Exception ex)
            {
                Log.Info("Update check failed (offline?): " + ex.Message);
                return null;
            }
        }

        /// <summary>Downloads the setup to %TEMP% and checks it. Returns its path; throws when the file is wrong.</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int> percent)
        {
            string file = Path.Combine(Path.GetTempPath(), $"LanPad-Setup-{info.Version}.exe");
            if (File.Exists(file)) File.Delete(file);
            using (var wc = NewClient())
            {
                wc.DownloadProgressChanged += (s, e) => percent?.Report(e.ProgressPercentage);
                await wc.DownloadFileTaskAsync(info.SetupUrl, file);
            }

            long length = new FileInfo(file).Length;
            string problem = null;
            if (info.Sha256 != null && FileSha256(file) != info.Sha256) problem = "SHA-256 does not match the release";
            else if (info.Sha256 == null && info.Size > 0 && length != info.Size) problem = $"size {length} instead of {info.Size}";
            if (problem != null)
            {
                File.Delete(file);
                throw new InvalidDataException("The downloaded update is damaged (" + problem + "). Try again.");
            }
            Log.Info($"Update v{info.Version} downloaded to {file} ({length:N0} bytes, " +
                     (info.Sha256 != null ? "SHA-256 ok)" : "no digest, size ok)"));
            return file;
        }

        /// <summary>
        /// Starts the setup (asks for admin). It closes LanPad, installs over it and starts LanPad again.
        /// False when the user said no to the admin prompt.
        /// </summary>
        public static bool RunSetup(string setupFile)
        {
            try
            {
                Process.Start(new ProcessStartInfo(setupFile, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LAUNCH=1")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                Log.Info("Update setup started: " + setupFile);
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                Log.Info("Update cancelled at the admin prompt");
                return false;
            }
        }
    }
}
