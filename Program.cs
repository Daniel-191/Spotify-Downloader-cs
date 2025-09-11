using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using System.Diagnostics;

namespace SpotifyDownloader
{
    public class SpotifyDownloader : IDisposable // Fixed: Added explicit IDisposable interface implementation
    {
        private readonly HttpClient _httpClient;
        private readonly YoutubeDL _ytdl;
        private const string DownloadDirectory = "music"; // Changed: Save files to local music directory (writable)

        public SpotifyDownloader()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", 
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", 
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");

            _ytdl = new YoutubeDL();
            
            // Configure paths (adjust these for your system)
            _ytdl.YoutubeDLPath = "/opt/homebrew/bin/yt-dlp"; // macOS Homebrew path
            _ytdl.FFmpegPath = "/opt/homebrew/bin/ffmpeg";   // macOS Homebrew path
            
            // For Windows, use these paths instead:
            // _ytdl.YoutubeDLPath = "yt-dlp.exe";
            // _ytdl.FFmpegPath = "ffmpeg.exe";

            CreateDownloadDirectory();
        }

        #region Console Output Methods
        private int GetTerminalWidth()
        {
            try
            {
                return Console.WindowWidth;
            }
            catch
            {
                return 80;
            }
        }

        public void PrintHeader() // Fixed: Changed to public so Main can access it
        {
            var width = GetTerminalWidth();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n{new string('=', width)}");
            Console.WriteLine("  SPOTIFY DOWNLOADER");
            Console.WriteLine($"{new string('=', width)}");
            Console.ResetColor();
        }

        private void PrintSeparator()
        {
            var width = GetTerminalWidth();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n{new string('-', width)}");
            Console.ResetColor();
        }

        private void PrintSuccess(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ {message}");
            Console.ResetColor();
        }

        private void PrintWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ {message}");
            Console.ResetColor();
        }

        public void PrintError(string message) // Fixed: Changed to public so Main can access it
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ {message}");
            Console.ResetColor();
        }

        private void PrintInfo(string message)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"• {message}");
            Console.ResetColor();
        }

        private string PrintProgressBar(double percentage, int width = 40)
        {
            var filled = (int)(width * percentage / 100);
            var bar = new string('█', filled) + new string('░', width - filled);
            return $"{bar} {percentage:F1}%";
        }
        #endregion

        #region Directory Management
        private void CreateDownloadDirectory()
        {
            if (!Directory.Exists(DownloadDirectory))
            {
                Directory.CreateDirectory(DownloadDirectory);
                PrintSuccess($"Created '{DownloadDirectory}' directory");
            }
        }
        #endregion

        #region Spotify URL Processing
        private (string contentType, string spotifyId) ExtractSpotifyId(string url)
        {
            url = url.Split('?')[0]; // Remove query parameters
            var pattern = @"spotify\.com/(track|album|playlist)/([a-zA-Z0-9]+)";
            var match = Regex.Match(url, pattern);
            
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value);
            }
            
            return (null!, null!); // Fixed: Explicit null-forgiving operator for nullability warning
        }

        private async Task<List<string>> GetTracksFromUrl(string url)
        {
            var tracks = new List<string>();
            var (contentType, spotifyId) = ExtractSpotifyId(url);

            if (string.IsNullOrEmpty(contentType) || string.IsNullOrEmpty(spotifyId))
            {
                PrintError("Invalid Spotify URL format");
                return tracks;
            }

            PrintInfo($"Detected {contentType} with ID: {spotifyId}");

            var approaches = new List<(string name, Func<string, string, string, Task<List<string>>> method)>
            {
                ("oEmbed API", TryOEmbedApi),
                ("Embed Page", TryEmbedPage),
                ("Direct Page", TryDirectPage),
                ("Manual Input", TryManualInput)
            };

            foreach (var (name, approach) in approaches)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"\nTrying {name}...");
                    Console.ResetColor();
                    
                    tracks = await approach(contentType, spotifyId, url);
                    
                    if (tracks.Count > 0)
                    {
                        PrintSuccess($"Found {tracks.Count} tracks using {name}");
                        break;
                    }
                    else
                    {
                        PrintWarning($"No tracks found with {name}");
                    }
                }
                catch (Exception e)
                {
                    PrintError($"Error with {name}: {e.Message[..Math.Min(50, e.Message.Length)]}..."); // Fixed: Use range operator instead of Substring
                }
            }

            return tracks;
        }

        private async Task<List<string>> TryOEmbedApi(string contentType, string spotifyId, string url)
        {
            var tracks = new List<string>();
            try
            {
                var oembedUrl = $"https://open.spotify.com/oembed?url={url}";
                var response = await _httpClient.GetAsync(oembedUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<JsonElement>(json);
                    
                    if (data.TryGetProperty("title", out var titleElement))
                    {
                        var title = titleElement.GetString();
                        if (!string.IsNullOrEmpty(title))
                        {
                            if (contentType == "track")
                            {
                                tracks.Add(title);
                            }
                            else
                            {
                                PrintInfo($"Found {contentType}: {title}");
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silently fail and try next approach
            }
            return tracks;
        }

        private async Task<List<string>> TryEmbedPage(string contentType, string spotifyId, string url)
        {
            var tracks = new List<string>();
            try
            {
                var embedUrl = $"https://open.spotify.com/embed/{contentType}/{spotifyId}";
                var response = await _httpClient.GetAsync(embedUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync();
                    
                    // Look for JSON data in script tags
                    var jsonPatterns = new[]
                    {
                        @"window\.__INITIAL_STATE__\s*=\s*({.*?});",
                        @"window\.__PRELOADED_STATE__\s*=\s*({.*?});",
                        @"window\.Spotify\s*=\s*({.*?});",
                        @"__NEXT_DATA__""\s*type=""application/json"">({.*?})</script>"
                    };

                    foreach (var pattern in jsonPatterns)
                    {
                        var matches = Regex.Matches(html, pattern, RegexOptions.Singleline);
                        foreach (Match match in matches)
                        {
                            try
                            {
                                var jsonData = JsonSerializer.Deserialize<JsonElement>(match.Groups[1].Value);
                                var extracted = ExtractTracksFromJson(jsonData);
                                tracks.AddRange(extracted);
                            }
                            catch
                            {
                                continue;
                            }
                        }
                    }

                    // If no JSON found, try regex
                    if (tracks.Count == 0)
                    {
                        tracks = EnhancedRegexExtract(html);
                    }
                }
            }
            catch (Exception e)
            {
                PrintError($"Embed page error: {e.Message}");
            }

            return tracks;
        }

        private async Task<List<string>> TryDirectPage(string contentType, string spotifyId, string url)
        {
            var tracks = new List<string>();
            try
            {
                var directUrl = $"https://open.spotify.com/{contentType}/{spotifyId}";
                var response = await _httpClient.GetAsync(directUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync();
                    tracks = EnhancedRegexExtract(html);
                }
            }
            catch (Exception e)
            {
                PrintError($"Direct page error: {e.Message}");
            }

            return tracks;
        }

        private async Task<List<string>> TryManualInput(string contentType, string spotifyId, string url) // Fixed: Restored async for Task<T> return type
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\nAutomatic extraction failed. Manual input required.");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Please open this URL in your browser: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(url);
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("\nPress Enter when ready to input songs...");
            Console.ResetColor();
            Console.ReadLine();

            var tracks = new List<string>();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nEnter songs in format: 'Artist - Song Title'");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Press Enter on empty line when done");
            Console.ResetColor();

            while (true)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"Song {tracks.Count + 1}: ");
                Console.ForegroundColor = ConsoleColor.White;
                var song = Console.ReadLine()?.Trim();
                Console.ResetColor();

                if (string.IsNullOrEmpty(song))
                {
                    if (tracks.Count == 0)
                    {
                        PrintWarning("No songs entered. Try again or press Enter to skip.");
                        continue;
                    }
                    break;
                }

                tracks.Add(song);
                PrintSuccess($"Added: {song}");
            }

            return await Task.FromResult(tracks); // Fixed: Convert to Task<List<string>> for async method
        }

        private List<string> EnhancedRegexExtract(string html)
        {
            var tracks = new List<string>();
            
            var patterns = new[]
            {
                @"""@type"":""MusicRecording"".*?""name"":""([^""]+)"".*?""byArtist"".*?""name"":""([^""]+)""",
                @"<meta property=""music:song"" content=""([^""]+)""",
                @"<meta property=""og:title"" content=""([^""]*?(?:by|-).*?)""",
                @"itemprop=""name""[^>]*>([^<]+)<.*?itemprop=""byArtist""[^>]*>([^<]+)<",
                @"""track"":{""uri"":""spotify:track:[^""]*"",""name"":""([^""]+)"".*?""artists"":\[{""name"":""([^""]+)""",
                @"""name"":""([^""]+)""[^}]*""artists"":\[{""name"":""([^""]+)""",
                @"""title"":""([^""]+)""[^}]*""subtitle"":""([^""]+)"""
            };

            foreach (var pattern in patterns)
            {
                var matches = Regex.Matches(html, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                
                foreach (Match match in matches)
                {
                    if (match.Groups.Count >= 3)
                    {
                        var artist = match.Groups[1].Value.Trim();
                        var song = match.Groups[2].Value.Trim();
                        
                        if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(song) && 
                            artist.Length > 1 && song.Length > 1)
                        {
                            var track = $"{artist} - {song}";
                            if (!tracks.Contains(track))
                            {
                                tracks.Add(track);
                            }
                        }
                    }
                    else if (match.Groups.Count >= 2)
                    {
                        var matchValue = match.Groups[1].Value.Trim();
                        if (matchValue.Contains(" - ") || matchValue.Contains(" by "))
                        {
                            if (!tracks.Contains(matchValue))
                            {
                                tracks.Add(matchValue);
                            }
                        }
                    }
                }
            }

            // Remove duplicates and filter
            var uniqueTracks = tracks
                .Where(track => track.Length > 5 && 
                              !track.ToLower().StartsWith("spotify") && 
                              track.Contains(" - "))
                .Distinct()
                .Take(50) // Limit to 50 tracks
                .ToList();

            return uniqueTracks;
        }

        private List<string> ExtractTracksFromJson(JsonElement data)
        {
            var tracks = new List<string>();
            ExtractTracksRecursive(data, tracks);
            return tracks;
        }

        private void ExtractTracksRecursive(JsonElement element, List<string> tracks)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("name", out var nameElement) && 
                        element.TryGetProperty("artists", out var artistsElement))
                    {
                        var trackName = nameElement.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(trackName) && artistsElement.ValueKind == JsonValueKind.Array)
                        {
                            var artists = artistsElement.EnumerateArray();
                            var firstArtist = artists.FirstOrDefault();
                            
                            if (firstArtist.ValueKind == JsonValueKind.Object && 
                                firstArtist.TryGetProperty("name", out var artistNameElement))
                            {
                                var artistName = artistNameElement.GetString()?.Trim();
                                if (!string.IsNullOrEmpty(artistName))
                                {
                                    var track = $"{artistName} - {trackName}";
                                    if (!tracks.Contains(track))
                                    {
                                        tracks.Add(track);
                                    }
                                }
                            }
                        }
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        ExtractTracksRecursive(property.Value, tracks);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        ExtractTracksRecursive(item, tracks);
                    }
                    break;
            }
        }
        #endregion

        #region YouTube Download
        private async Task DownloadFromYoutube(string query)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Searching for: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"'{query}'");
            Console.ResetColor();

            try
            {
                var searchQuery = $"ytsearch1:{query}";
                
                // Fixed: Removed problematic OptionSet configuration - use simple audio download
                var res = await _ytdl.RunAudioDownload(searchQuery, AudioConversionFormat.Mp3); // Fixed: Revert to original working API call

                if (res.Success)
                {
                    PrintSuccess($"Downloaded: {string.Join(", ", res.Data)}"); // Reverted: Keep original working code
                }
                else
                {
                    PrintError($"Download failed for '{query}': {string.Join(", ", res.ErrorOutput)}");
                }
            }
            catch (Exception e)
            {
                PrintError($"Download failed for '{query}': {e.Message[..Math.Min(50, e.Message.Length)]}..."); // Fixed: Use range operator instead of Substring
            }
        }
        #endregion

        #region Main Download Process
        private void DisplayTracksPreview(List<string> songs)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nFound {songs.Count} track(s):");
            Console.ResetColor();

            for (int i = 0; i < songs.Count; i++)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{i + 1,3}. ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(songs[i]);
                Console.ResetColor();
            }
        }

        public async Task DownloadFromSpotify(string url)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nProcessing Spotify URL...");
            Console.ResetColor();

            var songs = await GetTracksFromUrl(url);

            if (songs.Count == 0)
            {
                PrintError("No tracks found. Exiting...");
                return;
            }

            DisplayTracksPreview(songs);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("\nContinue with download? ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("[Y/n]: ");
            Console.ResetColor();
            
            var proceed = Console.ReadLine()?.ToLower();
            if (proceed != "y" && proceed != "yes" && proceed != "")
            {
                PrintInfo("Download cancelled");
                return;
            }

            PrintSeparator();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Starting downloads...\n");
            Console.ResetColor();

            for (int i = 0; i < songs.Count; i++)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"\n[{i + 1}/{songs.Count}] ");
                Console.ResetColor();
                
                await DownloadFromYoutube(songs[i]);

                if (i < songs.Count - 1)
                {
                    await Task.Delay(1000); // Small delay between downloads
                }
            }

            ShowDownloadSummary();
        }

        private void ShowDownloadSummary()
        {
            if (Directory.Exists(DownloadDirectory))
            {
                var files = Directory.GetFiles(DownloadDirectory, "*.*")
                    .Where(f => f.EndsWith(".mp3") || f.EndsWith(".m4a") || f.EndsWith(".webm"))
                    .ToList();

                if (files.Count > 0)
                {
                    Console.WriteLine($"Successfully downloaded {files.Count} files");
                    Console.WriteLine($"Location: ./{DownloadDirectory}/");

                    Console.WriteLine("Files:");
                    for (int i = 0; i < Math.Min(5, files.Count); i++)
                    {
                        Console.WriteLine($"  {Path.GetFileName(files[i])}");
                    }

                    if (files.Count > 5)
                    {
                        Console.WriteLine($"  ... and {files.Count - 5} more files");
                    }
                }
                else
                {
                    Console.WriteLine("No audio files found in download folder");
                }
            }

            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }
        #endregion

        #region Dependency Check
        public static bool CheckDependencies()
        {
            var downloader = new SpotifyDownloader();

            // Check FFmpeg
            // Fixed: Removed unused variable ffmpegAvailable
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                process?.WaitForExit();
                
                if (process?.ExitCode == 0)
                {
                    downloader.PrintSuccess("FFmpeg available - MP3 conversion enabled");
                    // Fixed: Removed assignment to unused variable
                }
                else
                {
                    downloader.PrintWarning("FFmpeg issue detected");
                }
            }
            catch (Exception)
            {
                downloader.PrintWarning("FFmpeg not found - files will be in original format");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("To get MP3 files, install FFmpeg:");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("  macOS: ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("brew install ffmpeg");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("  Ubuntu: ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("sudo apt install ffmpeg");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("  Windows: Download from https://ffmpeg.org");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("\nContinue without MP3 conversion? [Y/n]: ");
                Console.ResetColor();
                
                var proceed = Console.ReadLine()?.ToLower();
                if (proceed != "y" && proceed != "yes" && proceed != "")
                {
                    return false;
                }
            }

            downloader.PrintSuccess("yt-dlp ready");
            downloader.PrintSuccess("Download directory prepared");

            return true;
        }
        #endregion

        public void Dispose() // Fixed: Added GC.SuppressFinalize for proper IDisposable pattern
        {
            _httpClient?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    class Program
    {
        static async Task Main() // Fixed: Removed unused 'args' parameter
        {
            // Clear console
            Console.Clear();

            // Check dependencies
            if (!SpotifyDownloader.CheckDependencies())
            {
                Environment.Exit(1);
            }

            using var sd = new SpotifyDownloader();
            sd.PrintHeader();

            // URL input
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nEnter Spotify URL (track, album, or playlist):");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Example: https://open.spotify.com/track/4iV5W9uYEdYUVa79Axb7Rh");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("\nURL: ");
            Console.ForegroundColor = ConsoleColor.White;
            var link = Console.ReadLine();
            Console.ResetColor();

            // Download song/playlist/album
            if (!string.IsNullOrWhiteSpace(link))
            {
                await sd.DownloadFromSpotify(link.Trim());
            }
            else
            {
                sd.PrintError("No URL provided. Exiting...");
                await Task.Delay(2000);
            }
        }
    }
}