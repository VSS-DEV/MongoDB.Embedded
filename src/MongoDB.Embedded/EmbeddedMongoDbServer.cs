﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MongoDB.Driver;

namespace MongoDB.Embedded
{
    public class EmbeddedMongoDbServer : IDisposable
    {
        private Process _process;

        private Job _job; // = new Job();

        private readonly int _port;
        private readonly string _path;
        private string _db_path;
        private string _logPath;
        private readonly string _name;
        private readonly int _processEndTimeout;
        private string format = string.Empty;
        private readonly ManualResetEventSlim _gate = new ManualResetEventSlim(false);

        private bool os64()
        {
            if (Environment.Is64BitOperatingSystem)
                return true;
            else
                return false;
        }

        private string CheckWindowsVersion()
        {
            var windowsBuildNumber = libz.GetWindowsBuildNumber();
            if (windowsBuildNumber < 7600)
                throw new Exception(@"Минимальная версия WINDOWS - 7");

            if (windowsBuildNumber >= 10000)
            {
                if (os64())
                    return "mongod_5_x64.exe";
                else
                    return "mongod_3_x32.exe";
            }
            else if (windowsBuildNumber >=7600 || windowsBuildNumber >=9600 && windowsBuildNumber < 10000)
            {
                if (os64())
                    return "mongod_4_2_x64.exe";
                else
                    return "mongod_3_x32.exe";
            }
            return "";
        }

        private void CopyEmbededFiles(string FName)
        {
            using (var resourceStream =
                (typeof(EmbeddedMongoDbServer).Assembly.GetManifestResourceStream($"MongoDB.EmbeddedStandard2.{FName}") is null) ?
                typeof(EmbeddedMongoDbServer).Assembly.GetManifestResourceStream(typeof(EmbeddedMongoDbServer), FName) :
                typeof(EmbeddedMongoDbServer).Assembly.GetManifestResourceStream($"MongoDB.EmbeddedStandard2.{FName}"))
            using (var fileStream = new FileStream(Path.Combine(_path, FName), FileMode.Create, FileAccess.Write))
            {
                resourceStream.CopyTo(fileStream);
            }
            
        }

        private void CopyEmbededFiles(string FName, string SName)
        {
            using (var resourceStream =
                (typeof(EmbeddedMongoDbServer).Assembly.GetManifestResourceStream($"MongoDB.EmbeddedStandard2.{FName}") is null) ?
                typeof(EmbeddedMongoDbServer).Assembly.GetManifestResourceStream(typeof(EmbeddedMongoDbServer), FName) :
                typeof(EmbeddedMongoDbServer).Assembly.GetManifestResourceStream($"MongoDB.EmbeddedStandard2.{FName}"))
            using (var fileStream = new FileStream(Path.Combine(_path, SName + ".exe"), FileMode.Create, FileAccess.Write))
            {
                resourceStream.CopyTo(fileStream);
            }
        }

        public EmbeddedMongoDbServer(string logPath = null, string db_path = "db")
        {
            _db_path =$"{Environment.CurrentDirectory}\\{db_path}";
            _port = GetRandomUnusedPort();
            _logPath = $"{Environment.CurrentDirectory}\\{logPath}";
            _processEndTimeout = 10000;
            Directory.CreateDirectory(_db_path);
            KillMongoDbProcesses(_processEndTimeout);
            _name = RandomFileName(7);
            _path = Path.Combine(Path.GetTempPath(), RandomFileName(12));
            Directory.CreateDirectory(_path);

            switch (os64())
            {
                case false:
                    format += "--dbpath {0} --smallfiles --bind_ip 127.0.0.1 --storageEngine=mmapv1 --port {1}";
                    if (logPath != null)
                        format += " --journal --logpath {2}.log";
                    break;
                case true:
                    format += "--dbpath {0} --bind_ip 127.0.0.1 --port {1}";
                    if (logPath != null)
                        format += " --journal --logpath {2}.log";
                    break;
                default:
                    break;
            }

            Start_Server();
        }

        private void Start_Server()
        {
            CopyEmbededFiles(CheckWindowsVersion(), _name);
            _process = new Process
            {
                StartInfo =
            {
                Arguments = string.Format(format, _db_path, _port, _logPath),
                UseShellExecute = false,
                ErrorDialog = false,
                LoadUserProfile = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                FileName = Path.Combine(_path, _name + ".exe"),
                WorkingDirectory = _path
            }
            };

            _process.OutputDataReceived += ProcessOutputDataReceived;
            _process.ErrorDataReceived += ProcessErrorDataReceived;
            _process.Start();

            // _job.AddProcess(_process.Handle);

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _gate.Wait(8000);
        }

        public MongoClientSettings Settings
        {
            get { return new MongoClientSettings { Server = new MongoServerAddress("127.0.0.1", _port) }; }
        }

        public MongoClient Client
        {
            get { return new MongoClient(Settings); }
        }

        private static string RandomFileName(int length)
        {
            var chars = "abcdefghijklmnopqrstuvwxyz1234567890".ToCharArray();
            var data = new byte[1];
            var crypto = new RNGCryptoServiceProvider();
            crypto.GetNonZeroBytes(data);
            data = new byte[length];
            crypto.GetNonZeroBytes(data);
            var result = new StringBuilder(length);
            foreach (byte b in data)
                result.Append(chars[b % chars.Length]);

            return result.ToString();
        }

        private static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        protected void Dispose(bool disposing)
        {
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(_processEndTimeout);
                    }

                    _process.Dispose();
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(string.Format("Got exception when disposing the mongod server process msg = {0}", exception.Message));
                }

                _process = null;
            }

            if (_job != null)
            {
                _job.Dispose();
                _job = null;
            }

            if (Directory.Exists(_path))
                Directory.Delete(_path, true);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        ~EmbeddedMongoDbServer()
        {
            Dispose(false);
        }

        private void KillMongoDbProcesses(int millisTimeout)
        {
            var processesByName = Process.GetProcessesByName(_name);
            foreach (var process in processesByName)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(millisTimeout);
                    }
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(string.Format("Got exception when killing mongod.exe msg = {0}", exception.Message));
                }
            }
        }

        private void ProcessErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
                Trace.WriteLine(string.Format("Err - {0}", e.Data));
        }

        private void ProcessOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                if (e.Data.Contains("waiting for connections on port " + _port))
                    _gate.Set();

                Trace.WriteLine(string.Format("Output - {0}", e.Data));
            }
        }
    }
}