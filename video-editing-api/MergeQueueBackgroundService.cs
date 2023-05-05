using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using video_editing_api.Model.Collection;
using video_editing_api.Model.InputModel;
using video_editing_api.Service;
using video_editing_api.Service.DBConnection;

namespace video_editing_api
{
    public class MergeQueueBackgroundService : BackgroundService
    {
        private readonly string _baseUrl;
        private readonly IServiceProvider _serviceProvider;
        private readonly IHubContext<NotiHub> _hub;

        string ffmpegPath =
            @"C:\Users\Thai Long\Downloads\ffmpeg-2023-05-04-git-4006c71d19-full_build\ffmpeg-2023-05-04-git-4006c71d19-full_build\bin\ffmpeg.exe";

        string ffprobe =
            @"C:\Users\Thai Long\Downloads\ffmpeg-2023-05-04-git-4006c71d19-full_build\ffmpeg-2023-05-04-git-4006c71d19-full_build\bin\ffprobe.exe";

        private readonly IWebHostEnvironment _webHostEnvironment;

        public MergeQueueBackgroundService(IConfiguration configuration, IServiceProvider serviceProvider,
            IWebHostEnvironment webHostEnvironment,
            IHubContext<NotiHub> hub)
        {
            _webHostEnvironment = webHostEnvironment;
            _baseUrl = "https://store.cads.live/api";
            _serviceProvider = serviceProvider;
            _hub = hub;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (BackgroundQueue.MergeQueue.Count > 0)
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var dbClient = scope.ServiceProvider.GetRequiredService<IDbClient>();
                        var higlight = dbClient.GetHighlightVideoCollection();
                        Console.WriteLine("send " + DateTime.Now.ToString("dd-MM-yyy hh:mm:ss"));

                        MergeQueueInput input = BackgroundQueue.MergeQueue.Dequeue();
                        string message = string.Empty;
                        if (input.Status == 0)
                        {
                            message = await HandleSendServer(input, higlight);
                        }
                        else if (input.Status == 1)
                        {
                            message = await HandleSendServerNotMerge(input, higlight);
                        }

                        await _hub.Clients.Group(input.Username).SendAsync("noti",
                            input.Status == 0 ? "background_task" : "background_no_merge", message);
                        Console.WriteLine("done" + DateTime.Now.ToString("dd-MM-yyy hh:mm:ss"));
                    }
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }


        private async Task<string> HandleSendServer(MergeQueueInput input, IMongoCollection<HighlightVideo> _highlight)
        {
            HighlightVideo hl = new HighlightVideo();
            try
            {
                HttpClient client = new HttpClient();
                client.Timeout = TimeSpan.FromDays(1);
                //client.BaseAddress = new System.Uri(_baseUrl);
                input.JsonFile.merge = 1;
                var json = JsonConvert.SerializeObject(input.JsonFile);
                json = json.Replace("E", "e");
                Console.WriteLine("json " + json);

                hl = _highlight.Find(hl => hl.Id == input.IdHiglight).FirstOrDefault();

                // process video
                JObject config = JObject.Parse(json);
                string inputFolder = config["inforder"].ToString();
                string outputFolder = config["outfolder"].ToString();
                string logoFile = config["logo"][0]["file_name"] != null
                    ? config["logo"][0]["file_name"].ToString()
                    : null;
                object objSizeLogo = config["logo"][0]["size"];
                int logoX = (int) config["logo"][0]["position"]["x"];
                int logoY = (int) config["logo"][0]["position"]["y"];
                bool flag = false;
                string tempFilesList = "temp_files_list.txt";
                string videoCodec = "";
                string resolution = config["resolution"] != null ? config["resolution"].ToString() : null;
                string bitrate = config["resolution"] != null ? config["bitrate"].ToString() : null;
                string audioCodec = "";
                int videoBitrate = 0;
                int audioBitrate = 0;
                int width = 0;
                int height = 0;
                using (StreamWriter file = new StreamWriter(tempFilesList))
                {
                    int index = 0;
                    // check xem có video nào không logo không
                    foreach (var eventObj in config["event"])
                    {
                        if (eventObj["logo"] != null && eventObj["logo"].ToString().Contains("1"))
                        {
                            string eventUrl = eventObj["file_name"].ToString();
                            string eventFileName = eventUrl.Split("/")[eventUrl.Split("/").Length - 3] + ".mp4";
                            await DownloadFileAsync(eventUrl, eventFileName);
                            string eventOutputFileName =
                                Path.GetFileNameWithoutExtension(eventFileName) + "_with_logo.mp4";
                            await AddLogoToVideoAsync(config,eventFileName, eventOutputFileName, logoFile, logoX, logoY);
                            file.WriteLine($"file '{eventOutputFileName}'");
                            (videoCodec, audioCodec, videoBitrate, audioBitrate, width, height) =
                                GetVideoInfo($"./{eventOutputFileName}");
                            flag = true;
                            break;
                        }
                        else
                        {
                        }
                    }

                    foreach (var eventObj in config["event"])
                    {
                        string eventUrl = eventObj["file_name"].ToString();
                        string eventFileName = eventUrl.Split("/")[eventUrl.Split("/").Length - 3] + ".mp4";
                        await DownloadFileAsync(eventUrl, eventFileName);
                        if (eventObj["logo"] != null && eventObj["logo"].ToString().Contains("1"))
                        {
                            string eventOutputFileName =
                                Path.GetFileNameWithoutExtension(eventFileName) + "_with_logo.mp4";
                            if (!File.Exists("./" + eventOutputFileName))
                            {
                                await AddLogoToVideoAsync(config,eventFileName, eventOutputFileName, logoFile, logoX, logoY);
                                file.WriteLine($"file '{eventOutputFileName}'");
                            }
                        }
                        else
                        {
                            string eventOutputFileName =
                                Path.GetFileNameWithoutExtension(eventFileName) + "_with_no_logo.mp4";
                            if (flag)
                            {
                                if (!File.Exists("./" + eventOutputFileName))
                                {
                                    await ConvertVideo("./" + eventFileName, "./" + eventOutputFileName, videoCodec,
                                        audioCodec,
                                        videoBitrate, audioBitrate, width, height);
                                    file.WriteLine($"file '{eventOutputFileName}'");
                                }
                                else
                                    file.WriteLine($"file '{eventOutputFileName}'");
                            }
                            else
                                file.WriteLine($"file '{eventFileName}'");
                        }
                    }
                }

                string fileName = Guid.NewGuid().ToString() + ".mp4";
                await MergeVideosAsync(resolution,tempFilesList, "./videos/" + fileName);
                if (true)
                {
                    // ConcatResultModel model = JsonConvert.DeserializeObject<ConcatResultModel>(result);
                    if (hl != null)                                                                                     
                    {
                        hl.mp4 = "https://localhost:44394/videos/" + fileName;
                        hl.ts = "https://localhost:44394/videos/" + fileName;
                        hl.Status = SystemConstants.HighlightStatusSucceed;
                    }

                    await _highlight.ReplaceOneAsync(hl => hl.Id == input.IdHiglight, hl);
                }
                else
                {
                    Console.WriteLine("error server thầy" + DateTime.Now.ToString("dd-MM-yyy hh:mm:ss"));
                    hl.Status = SystemConstants.HighlightStatusFailed;
                    await _highlight.ReplaceOneAsync(hl => hl.Id == input.IdHiglight, hl);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("error" + DateTime.Now.ToString("dd-MM-yyy hh:mm:ss"));
                hl = _highlight.Find(hl => hl.Id == input.IdHiglight).FirstOrDefault();
                hl.Status = SystemConstants.HighlightStatusFailed;
                await _highlight.ReplaceOneAsync(hl => hl.Id == input.IdHiglight, hl);
            }

            return JsonConvert.SerializeObject(hl);
        }

        private async Task ConvertVideo(string input, string output, string videoCodec, string audioCodec,
            int videoBitrate,
            int audioBitrate, int width, int height)
        {
            if (File.Exists($"{output}"))
            {
                return;
            }

            string args =
                $"-i {input} -c:v {videoCodec} -c:a {audioCodec} -b:v {videoBitrate}k -b:a {audioBitrate}k -s {width}x{height} {output}";
            await ExecuteFFmpegAsync(args);
        }

        private async Task AddLogoToVideoAsync(JObject config , string inputFile, string outputFile, string logoFile, int x, int y)
        {
            if (File.Exists($"./{outputFile}"))
            {
                return;
            }

            string arguments;
            if (config["logo"][0] != null && config["logo"][0]["size"] != null)
            { 
                String width = config["logo"][0]["size"][0].ToString();
                String height =  config["logo"][0]["size"][1].ToString();
                arguments =
                    $"-hwaccel cuda -i \"{inputFile}\" -i \"{logoFile}\" -filter_complex \"[0:v]scale=1920:1080[bg];[1:v]scale={width}:{height}[logo_resized];[bg][logo_resized]overlay={x}:{y}\" -c:v libx264 -crf 23 -preset veryfast -c:a copy \"{outputFile}\"";

            }
            else
            // string arguments = $"-hwaccel cuda -i \"{inputFile}\" -i \"{logoFile}\" -filter_complex \"[0:v][1:v]overlay=340:398\" -c:v libx264 -crf 23 -preset veryfast -c:a copy \"{outputFile}\"";
             arguments =
                 $"-hwaccel cuda -i \"{inputFile}\" -i \"{logoFile}\" -filter_complex \"[0:v]scale=1920:1080[bg];[1:v][bg]overlay={x}:{y}\" -c:v libx264 -crf 23 -preset veryfast -c:a copy \"{outputFile}\"";

            await ExecuteFFmpegAsync(arguments);
        }

        private async Task MergeVideosAsync(string resolution ,string fileList, string outputFile)
        {
            string arguments = resolution != null ?  $"-f concat -safe 0 -hwaccel cuda -i \"{fileList}\" -vf \"scale={resolution.Split(":")[0].ToString()}:{resolution.Split(":")[1].ToString()}\" -c:v libx264 -crf 23 -preset veryfast -c:a copy \"{outputFile}\""
                : $"-f concat -safe 0 -hwaccel cuda -i \"{fileList}\" -c copy \"{outputFile}\"" 
                ;
            // $"-f concat -safe 0 -i \"{fileList}\" -filter:v \"scale=1920:1080\" -c:v libx264 -crf 23 -preset veryfast -c:a copy \"{outputFile}\"";
            await ExecuteFFmpegAsync(arguments);
        }
    
        private async Task DownloadFileAsync(string url, string fileName)
        {
            if (File.Exists($"./{fileName}"))
            {
                return;
            }

            using (HttpClient client = new HttpClient())
            using (HttpResponseMessage response = await client.GetAsync(url))
            using (Stream stream = await response.Content.ReadAsStreamAsync())
            using (FileStream fileStream = new FileStream(fileName, FileMode.Create))
            {
                await stream.CopyToAsync(fileStream);
            }
        }


        private (string videoCodec, string audioCodec, int videoBitrate, int audioBitrate, int width, int height)
            GetVideoInfo(string videoPath)
        {
            string ffprobe =
                @"C:\Users\Thai Long\Downloads\ffmpeg-2023-05-04-git-4006c71d19-full_build\ffmpeg-2023-05-04-git-4006c71d19-full_build\bin\ffprobe.exe"; // Make sure ffprobe is in your system PATH

            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v quiet -print_format json -show_streams \"{videoPath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            JObject json = JObject.Parse(output);

            string videoCodec = "";
            string audioCodec = "";
            int videoBitrate = 0;
            int audioBitrate = 0;
            int width = 0;
            int height = 0;

            foreach (var stream in json["streams"])
            {
                if (stream["codec_type"].ToString() == "video")
                {
                    videoCodec = stream["codec_name"].ToString();
                    videoBitrate = int.Parse(stream["bit_rate"].ToString()) / 1000;
                    width = int.Parse(stream["width"].ToString());
                    height = int.Parse(stream["height"].ToString());
                }
                else if (stream["codec_type"].ToString() == "audio")
                {
                    audioCodec = stream["codec_name"].ToString();
                    audioBitrate = int.Parse(stream["bit_rate"].ToString()) / 1000;
                }
            }

            return (videoCodec, audioCodec, videoBitrate, audioBitrate, width, height);
        }


        async Task ExecuteFFmpegAsync(string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath, // Update this with the correct path to ffmpeg.exe
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (Process process = new Process {StartInfo = startInfo})
            {
                process.Start();

                // Read the output and error streams to avoid hanging the process
                process.OutputDataReceived += (sender, e) => Console.WriteLine(e.Data);
                process.ErrorDataReceived += (sender, e) => Console.WriteLine(e.Data);

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for the process to exit
                await process.WaitForExitAsync();
            }
        }

        private async Task<string> HandleSendServerNotMerge(MergeQueueInput input,
            IMongoCollection<HighlightVideo> _highlight)
        {
            HighlightVideo hl = new HighlightVideo();
            try
            {
                HttpClient client = new HttpClient();
                client.Timeout = TimeSpan.FromDays(1);
                client.BaseAddress = new System.Uri(_baseUrl);
                input.JsonFile.merge = 0;
                var json = JsonConvert.SerializeObject(input.JsonFile);
                json = json.Replace("E", "e");
                Console.WriteLine("json " + json);

                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync("/projects/merge", httpContent);


                hl = _highlight.Find(hl => hl.Id == input.IdHiglight).FirstOrDefault();

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadAsStringAsync();
                    var listRes = JsonConvert.DeserializeObject<NotConcatResultModel>(result);
                    if (hl != null)
                    {
                        hl.list_mp4 = listRes.mp4;
                        hl.list_ts = listRes.ts;
                        hl.Status = SystemConstants.HighlightStatusSucceed;
                    }

                    await _highlight.ReplaceOneAsync(hl => hl.Id == input.IdHiglight, hl);
                }
                else
                {
                    Console.WriteLine("error server thầy" + DateTime.Now.ToString("dd-MM-yyy hh:mm:ss"));
                    hl.Status = SystemConstants.HighlightStatusFailed;
                    await _highlight.ReplaceOneAsync(hl => hl.Id == input.IdHiglight, hl);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("error" + DateTime.Now.ToString("dd-MM-yyy hh:mm:ss"));
                hl = _highlight.Find(hl => hl.Id == input.IdHiglight).FirstOrDefault();
                hl.Status = SystemConstants.HighlightStatusFailed;
                await _highlight.ReplaceOneAsync(hl => hl.Id == input.IdHiglight, hl);
            }

            return JsonConvert.SerializeObject(hl);
        }
    }
}