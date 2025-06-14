using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using NetMQ;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;

class NamedPipeLanguageClient
{
    static void Main(string[] args)
    {
        var server = new Process();
        server.StartInfo.UseShellExecute = false;
        server.StartInfo.CreateNoWindow = true;
        if (Environment.OSVersion.Platform == PlatformID.Unix)
        {
            server.StartInfo.FileName = "mono";
            server.StartInfo.Arguments = AppDomain.CurrentDomain.BaseDirectory + "../IntelliSense/LanguageServerEngine.exe";
        }
        else
        {
            server.StartInfo.FileName = AppDomain.CurrentDomain.BaseDirectory + "..\\IntelliSense\\LanguageServerEngine.exe";
        }

        server.StartInfo.EnvironmentVariables.Add("parentId", Process.GetCurrentProcess().Id.ToString());

        server.Start();
        RunClient().Wait();
    }

    static async Task RunClient()
    {
        var pipeName = "language-pipe";
        var socket = new NetMQ.Sockets.ResponseSocket();
        socket.Bind("tcp://127.0.0.1:5557");

        using (var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            Console.WriteLine("Connecting to server...");
            await pipeClient.ConnectAsync();
            Console.WriteLine("Connected!");
            using (var writer = new StreamWriter(pipeClient, new UTF8Encoding(false)) { AutoFlush = true })
            using (var reader = new StreamReader(pipeClient, new UTF8Encoding(false)))
            {
                while (true)
                {
                    var json = socket.ReceiveFrameString();
                    Console.WriteLine(json);

                    // didOpen and didChange do not return answer, so this if is neccesary
                    if (json.Contains("didOpen") || json.Contains("didChange"))
                    {
                        socket.SendFrame("code GREEN!");
                        await SendMessage(writer, json);
                        continue;
                    }

                    await SendMessage(writer, json);
                    Console.WriteLine("Sent initialize request.");

                    var response = await ReadMessage(reader.BaseStream);
                    Console.WriteLine("Received:\n" + response);

                    socket.SendFrame(response);
                }
            }
        }
    }

    static async Task SendMessage(StreamWriter writer, string json)
    {
        var contentBytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {contentBytes.Length}\r\n\r\n";
        await writer.WriteAsync(header + json);
    }

    static async Task<string> ReadMessage(Stream stream)
    {
        var headerBuilder = new MemoryStream();
        byte[] buffer = new byte[1];

        // read until \r\n\r\n
        int state = 0; // 0 = nothing, 1 = \r, 2 = \r\n, 3 = \r\n\r, 4 = \r\n\r\n
        while (state < 4)
        {
            int read = await stream.ReadAsync(buffer, 0, 1);
            if (read == 0) break;

            headerBuilder.WriteByte(buffer[0]);

            switch (state)
            {
                case 0: state = buffer[0] == '\r' ? 1 : 0; break;
                case 1: state = buffer[0] == '\n' ? 2 : 0; break;
                case 2: state = buffer[0] == '\r' ? 3 : 0; break;
                case 3: state = buffer[0] == '\n' ? 4 : 0; break;
            }
        }

        string headers = Encoding.ASCII.GetString(headerBuilder.ToArray());
        int contentLength = 0;

        foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:"))
            {
                contentLength = int.Parse(line.Substring("Content-Length:".Length).Trim());
            }
        }

        if (contentLength == 0) return null;

        byte[] contentBuffer = new byte[contentLength];
        int bytesRead = 0;
        while (bytesRead < contentLength)
        {
            int n = await stream.ReadAsync(contentBuffer, bytesRead, contentLength - bytesRead);
            if (n == 0) break;
            bytesRead += n;
        }

        return Encoding.UTF8.GetString(contentBuffer, 0, bytesRead);
    }

}
