using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Semaphore
{
    sealed class HookServer
    {
        Thread thread;
        volatile bool stopped;

        public event Action<string> Received;

        public void Start()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "HookServer" };
            thread.Start();
        }

        public void Stop()
        {
            stopped = true;
        }

        // The next pipe instance is created before the current message is read, so clients never
        // find the pipe missing, and messages are handled strictly in arrival order.
        void Loop()
        {
            NamedPipeServerStream server = null;
            while (!stopped)
            {
                try
                {
                    if (server == null)
                        server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 8, PipeTransmissionMode.Byte);
                    server.WaitForConnection();
                    NamedPipeServerStream connected = server;
                    server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 8, PipeTransmissionMode.Byte);
                    Handle(connected);
                }
                catch (Exception ex)
                {
                    Log.Write("Pipe error: " + ex.Message);
                    if (server != null) { server.Dispose(); server = null; }
                    Thread.Sleep(250);
                }
            }
        }

        void Handle(NamedPipeServerStream server)
        {
            try
            {
                using (server)
                using (var reader = new StreamReader(server, new UTF8Encoding(false)))
                {
                    string text = reader.ReadToEnd();
                    var handler = Received;
                    if (handler != null && text.Length > 0)
                        handler(text);
                }
            }
            catch (Exception ex) { Log.Write("Pipe read error: " + ex.Message); }
        }
    }
}
