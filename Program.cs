//
// Copyright (c) Roland Pihlakas 2019 - 2022
// roland@simplify.ee
//
// Roland Pihlakas licenses this file to you under the GNU Lesser General Public License, ver 2.1.
// See the LICENSE file for more information.
//

#define ASYNC
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Dasync.Collections;
using Microsoft.Extensions.Configuration;
using myoddweb.directorywatcher;
using myoddweb.directorywatcher.interfaces;
using Nito.AspNetBackgroundTasks;
using Nito.AsyncEx;
using NReco.Text;

namespace AsyncToSyncCodeRoundtripSynchroniserMonitor
{
#pragma warning disable S2223   //Warning	S2223	Change the visibility of 'xxx' or make it 'const' or 'readonly'.
    internal static class Global
    {
        public static IConfigurationRoot Configuration;
        public static readonly CancellationTokenSource CancellationToken = new CancellationTokenSource();



        public static bool UseIdlePriority = false;

        public static bool ShowErrorAlerts = true;
        public static bool LogInitialScan = false;
        public static bool LogToFile = false;
        public static bool AddTimestampToNormalLogEntries = true;

        public static int RetryCountOnSrcFileOpenError = 10;

        public static long MaxFileSizeMB = 2048;


        public static HashSet<string> WatchedCodeExtension = new HashSet<string>() { "cs", "py" };
        public static HashSet<string> WatchedResXExtension = new HashSet<string>() { "resx" };

        public static HashSet<string> ExcludedExtensions = new HashSet<string>() { "*~", "tmp" };

        public static List<string> IgnorePathsStartingWithList = new List<string>();
        public static List<string> IgnorePathsContainingList = new List<string>();
        public static List<string> IgnorePathsEndingWithList = new List<string>();

        public static bool IgnorePathsContainingACHasAny = false;
        public static AhoCorasickDoubleArrayTrie<bool> IgnorePathsContainingAC = new AhoCorasickDoubleArrayTrie<bool>();

        public static string AsyncPath = "";
        public static string SyncPath = "";

        public static long AsyncPathMinFreeSpace = 0;
        public static long SyncPathMinFreeSpace = 0;

        public static bool Bidirectional = true;
        public static bool? CaseSensitiveFilenames = null;   //null: default behaviour depending on OS



        internal static readonly AsyncLockQueueDictionary<string> FileOperationLocks = new AsyncLockQueueDictionary<string>();
        //internal static readonly AsyncLock FileOperationAsyncLock = new AsyncLock();
        internal static readonly AsyncSemaphore FileOperationSemaphore = new AsyncSemaphore(2);     //allow 2 concurrent file synchronisations: while one is finishing the write, the next one can start the read
    }
#pragma warning restore S2223

    class DummyFileSystemEvent : IFileSystemEvent
    {
        [DebuggerStepThrough]
        public DummyFileSystemEvent(FileSystemInfo fileSystemInfo)
        {
            FileSystemInfo = fileSystemInfo;
            FullName = fileSystemInfo.FullName;
            Name = fileSystemInfo.Name;
            Action = EventAction.Added;
            Error = EventError.None;
            DateTimeUtc = DateTime.UtcNow;
            IsFile = true;
        }

        public FileSystemInfo FileSystemInfo { [DebuggerStepThrough]get; }
        public string FullName { [DebuggerStepThrough]get; }
        public string Name { [DebuggerStepThrough]get; }
        public EventAction Action { [DebuggerStepThrough]get; }
        public EventError Error { [DebuggerStepThrough]get; }
        public DateTime DateTimeUtc { [DebuggerStepThrough]get; }
        public bool IsFile { [DebuggerStepThrough]get; }

        [DebuggerStepThrough]
        public bool Is(EventAction action)
        {
            return action == Action;
        }
    }

    internal class Program
    {
        //let null char mark start and end of a filename
        //https://stackoverflow.com/questions/54205087/how-can-i-create-a-file-with-null-bytes-in-the-filename
        //https://stackoverflow.com/questions/1976007/what-characters-are-forbidden-in-windows-and-linux-directory-names
        //https://serverfault.com/questions/242110/which-common-characters-are-illegal-in-unix-and-windows-filesystems
        public static readonly string NullChar = new string(new char[] { (char)0 });

        public static readonly string DirectorySeparatorChar = new string(new char[] { Path.DirectorySeparatorChar });


        private static byte[] GetHash(string inputString)
        {
#pragma warning disable SCS0006     //Warning	SCS0006	Weak hashing function
            HashAlgorithm algorithm = MD5.Create();
#pragma warning restore SCS0006
            return algorithm.ComputeHash(Encoding.UTF8.GetBytes(inputString));
        }

        public static string GetHashString(string inputString)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte b in GetHash(inputString))
                sb.Append(b.ToString("X2"));

            return sb.ToString();
        }

        private static void Main()
        {
            //var environmentName = Environment.GetEnvironmentVariable("Hosting:Environment");

            var configBuilder = new ConfigurationBuilder()
                //.SetBasePath(System.IO.Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                //.AddJsonFile($"appsettings.{environmentName}.json", true)
                //.AddEnvironmentVariables()
                ;

            var config = configBuilder.Build();
            Global.Configuration = config;


            var fileConfig = config.GetSection("Files");


            Global.UseIdlePriority = fileConfig.GetTextUpper("UseIdlePriority") == "TRUE";   //default is false

            
            Global.ShowErrorAlerts = fileConfig.GetTextUpper("ShowErrorAlerts") != "FALSE";   //default is true
            Global.LogInitialScan = fileConfig.GetTextUpper("LogInitialScan") == "TRUE";   //default is false
            Global.LogToFile = fileConfig.GetTextUpper("LogToFile") == "TRUE";   //default is false
            Global.AddTimestampToNormalLogEntries = fileConfig.GetTextUpper("AddTimestampToNormalLogEntries") != "FALSE";   //default is true


            Global.MaxFileSizeMB = fileConfig.GetLong("MaxFileSizeMB") ?? Global.MaxFileSizeMB;
            Global.RetryCountOnSrcFileOpenError = (int?)fileConfig.GetLong("RetryCountOnSrcFileOpenError") ?? Global.RetryCountOnSrcFileOpenError;


            Global.Bidirectional = fileConfig.GetTextUpper("Bidirectional") != "FALSE";   //default is true

            if (!string.IsNullOrWhiteSpace(fileConfig.GetTextUpper("CaseSensitiveFilenames")))   //default is null
                Global.CaseSensitiveFilenames = fileConfig.GetTextUpper("CaseSensitiveFilenames") == "TRUE";


            Global.AsyncPath = Extensions.GetDirPathWithTrailingSlash(fileConfig.GetTextUpperOnWindows(Global.CaseSensitiveFilenames, "AsyncPath"));
            Global.SyncPath = Extensions.GetDirPathWithTrailingSlash(fileConfig.GetTextUpperOnWindows(Global.CaseSensitiveFilenames, "SyncPath"));

            Global.AsyncPathMinFreeSpace = fileConfig.GetLong("AsyncPathMinFreeSpace") ?? 0;
            Global.SyncPathMinFreeSpace = fileConfig.GetLong("SyncPathMinFreeSpace") ?? 0;

            Global.WatchedCodeExtension = new HashSet<string>(fileConfig.GetListUpperOnWindows(Global.CaseSensitiveFilenames, "WatchedCodeExtensions", "WatchedCodeExtension"));
            Global.WatchedResXExtension = new HashSet<string>(fileConfig.GetListUpperOnWindows(Global.CaseSensitiveFilenames, "WatchedResXExtensions", "WatchedResXExtension"));

            //this would need Microsoft.Extensions.Configuration and Microsoft.Extensions.Configuration.Binder packages
            Global.ExcludedExtensions = new HashSet<string>(fileConfig.GetListUpperOnWindows(Global.CaseSensitiveFilenames, "ExcludedExtensions", "ExcludedExtension"));   //NB! UpperOnWindows

            Global.IgnorePathsStartingWithList = fileConfig.GetListUpperOnWindows(Global.CaseSensitiveFilenames, "IgnorePathsStartingWith", "IgnorePathStartingWith");   //NB! UpperOnWindows
            Global.IgnorePathsContainingList = fileConfig.GetListUpperOnWindows(Global.CaseSensitiveFilenames, "IgnorePathsContaining", "IgnorePathContaining");   //NB! UpperOnWindows
            Global.IgnorePathsEndingWithList = fileConfig.GetListUpperOnWindows(Global.CaseSensitiveFilenames, "IgnorePathsEndingWith", "IgnorePathEndingWith");   //NB! UpperOnWindows

            var ACInput = Global.IgnorePathsStartingWithList.Select(x => new KeyValuePair<string, bool>(NullChar + x, false))
                .Concat(Global.IgnorePathsContainingList.Select(x => new KeyValuePair<string, bool>(x, false)))
                .Concat(Global.IgnorePathsEndingWithList.Select(x => new KeyValuePair<string, bool>(x + NullChar, false)))
                .ToList();

            if (ACInput.Any())  //needed to avoid exceptions
            {
                Global.IgnorePathsContainingACHasAny = true;
                Global.IgnorePathsContainingAC.Build(ACInput);
            }


            var pathHashes = "";
            pathHashes += "_" + GetHashString(Global.AsyncPath);
            pathHashes += "_" + GetHashString(Global.SyncPath);

            //NB! prevent multiple instances from starting on same directories
            using (Mutex mutex = new Mutex(false, "Global\\AsyncToSyncCodeRoundtripSynchroniserMonitor_" + pathHashes))
            {
                if (!mutex.WaitOne(0, false))
                {
                    Console.WriteLine("Instance already running");
                }
                else
                {
                    MainTask().Wait();
                }
            }
        }

        private static async Task MainTask()
        {
            try
            {
                //Console.WriteLine(Environment.Is64BitProcess ? "x64 version" : "x86 version");
                Console.WriteLine("Press Ctrl+C to stop the monitors.");


                if (Global.UseIdlePriority)
                {
                    try
                    {
                        var CurrentProcess = Process.GetCurrentProcess();
                        CurrentProcess.PriorityClass = ProcessPriorityClass.Idle;
                        CurrentProcess.PriorityBoostEnabled = false;

                        if (ConfigParser.IsWindows)
                        {
                            WindowsDllImport.SetIOPriority(CurrentProcess.Handle, WindowsDllImport.PROCESSIOPRIORITY.PROCESSIOPRIORITY_VERY_LOW);
                        }
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Unable to set idle priority.");
                    }
                }


                ThreadPool.SetMaxThreads(16, 16);   //TODO: config


                //start the monitor.
                using (var watch = new Watcher())
                {
                    watch.Add(new Request(Extensions.GetLongPath(Global.SyncPath), recursive: true));

                    if (Global.Bidirectional)
                    {
                        watch.Add(new Request(Extensions.GetLongPath(Global.AsyncPath), recursive: true));
                    }


                    // prepare the console watcher so we can output pretty messages.
                    var consoleWatch = new ConsoleWatch(watch);


                    //start watching
                    //NB! start watching before synchronisation
                    watch.Start();


                    var initialSyncMessageContext = new Context(
                        eventObj: null,
                        token: Global.CancellationToken.Token,
                        isSyncPath: false,   //unused here
                        isInitialScan: true
                    );


                    BackgroundTaskManager.Run(async () =>
                    {
                        await ConsoleWatch.AddMessage(ConsoleColor.White, "Doing initial synchronisation...", initialSyncMessageContext);

                        await ScanFolders(initialSyncMessageContext: initialSyncMessageContext);

                        BackgroundTaskManager.Run(async () =>
                        {
                            await InitialSyncCountdownEvent.WaitAsync(Global.CancellationToken.Token);

                            //if (!Global.CancellationToken.IsCancellationRequested)
                                await ConsoleWatch.AddMessage(ConsoleColor.White, "Done initial synchronisation...", initialSyncMessageContext);
                        });

                    });     //BackgroundTaskManager.Run(async () =>


                    //listen for the Ctrl+C 
                    await WaitForCtrlC();

                    Console.WriteLine("Stopping...");

                    //stop everything.
                    watch.Stop();

                    Console.WriteLine("Exiting...");

                    GC.KeepAlive(consoleWatch);


                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                await WriteException(ex);
            }
        }   //private static async Task MainTask()

        private static readonly AsyncCountdownEvent InitialSyncCountdownEvent = new AsyncCountdownEvent(1);

        private static async Task ScanFolders(Context initialSyncMessageContext)
        {
            //1. Do initial synchronisation from sync to async folder   //TODO: config for enabling and ordering of this operation
            await ScanFolder(Global.SyncPath, "*.*", initialSyncMessageContext: initialSyncMessageContext);     //NB! use *.* in order to sync resx files also

            if (Global.Bidirectional)
            {
                //2. Do initial synchronisation from async to sync folder   //TODO: config for enabling and ordering of this operation
                await ScanFolder(Global.AsyncPath, "*.*", initialSyncMessageContext: initialSyncMessageContext);     //NB! use *.* in order to sync resx files also
            }

            if (initialSyncMessageContext?.IsInitialScan == true)
                InitialSyncCountdownEvent.Signal();
        }

        private static async Task ScanFolder(string path, string extension, Context initialSyncMessageContext)
        {
            var fileInfos = ProcessSubDirs(new DirectoryInfo(Extensions.GetLongPath(path)), extension, initialSyncMessageContext: initialSyncMessageContext);
            await fileInfos.ForEachAsync(fileInfo => 
            {
                if (initialSyncMessageContext?.IsInitialScan == true)
                    InitialSyncCountdownEvent.AddCount();

                BackgroundTaskManager.Run(async () => 
                {
                    await ConsoleWatch.OnAddedAsync
                    (
                        new DummyFileSystemEvent(fileInfo),
                        Global.CancellationToken.Token,
                        initialSyncMessageContext?.IsInitialScan == true
                    );

                    if (initialSyncMessageContext?.IsInitialScan == true)
                        InitialSyncCountdownEvent.Signal();
                });
            });
        }

        private static IAsyncEnumerable<FileInfo> ProcessSubDirs(DirectoryInfo srcDirInfo, string searchPattern, int recursionLevel = 0, Context initialSyncMessageContext = null)
        {
            return new AsyncEnumerable<FileInfo>(async yield => {


                if (Global.LogInitialScan && initialSyncMessageContext?.IsInitialScan == true)
                    await ConsoleWatch.AddMessage(ConsoleColor.Blue, "Scanning folder " + Extensions.GetLongPath(srcDirInfo.FullName), initialSyncMessageContext);


#if false //this built-in functio will throw IOException in case some subfolder is an invalid reparse point
                return new DirectoryInfo(sourceDir)
                    .GetFiles(searchPattern, SearchOption.AllDirectories);
#else

                //Directory.GetFileSystemEntries would not help here since it returns only strings, not FileInfos

                //TODO: under Windows10 use https://github.com/ljw1004/uwp-desktop for true async dirlists

                FileInfo[] fileInfos;
                try
                {
                    fileInfos = await Extensions.FSOperation
                    (
                        () => srcDirInfo.GetFiles(searchPattern, SearchOption.TopDirectoryOnly),
                        Global.CancellationToken.Token
                    );
                }
                catch (Exception ex) when (ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
                {
                    //UnauthorizedAccessException can also occur when a folder was just created, but it can still be ignored here since then file add handler will take care of that folder

                    fileInfos = Array.Empty<FileInfo>();
                }

                foreach (var fileInfo in fileInfos)
                {
                    await yield.ReturnAsync(fileInfo);
                }


                DirectoryInfo[] dirInfos;
#pragma warning disable S2327   //Warning	S2327	Combine this 'try' with the one starting on line XXX.
                try
                {
                    dirInfos = await Extensions.FSOperation
                    (
                        () => srcDirInfo.GetDirectories("*", SearchOption.TopDirectoryOnly),
                        Global.CancellationToken.Token
                    );
                }
                catch (Exception ex) when (ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
                {
                    //UnauthorizedAccessException can also occur when a folder was just created, but it can still be ignored here since then file add handler will take care of that folder

                    dirInfos = Array.Empty<DirectoryInfo>();
                }
#pragma warning restore S2327

                foreach (var dirInfo in dirInfos)
                {
                    //TODO: option to follow reparse points
                    if ((dirInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        continue;


                    var nonFullNameInvariantWithLeadingSlash = DirectorySeparatorChar + Extensions.GetDirPathWithTrailingSlash(ConsoleWatch.GetNonFullName(dirInfo.FullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames)));
                    if (
                        //Global.IgnorePathsStartingWith.Any(x => nonFullNameInvariantWithLeadingSlash.StartsWith(x))
                        //|| Global.IgnorePathsContaining.Any(x => nonFullNameInvariantWithLeadingSlash.Contains(x))
                        //|| Global.IgnorePathsEndingWith.Any(x => nonFullNameInvariantWithLeadingSlash.EndsWith(x))
                        Global.IgnorePathsContainingACHasAny  //needed to avoid exceptions
                        && Global.IgnorePathsContainingAC.ParseText(NullChar + nonFullNameInvariantWithLeadingSlash/* + NullChar*/).Any() //NB! no NullChar appended to end since it is dir path not complete file path
                    )
                    {
                        continue;
                    }


                    var subDirFileInfos = ProcessSubDirs(dirInfo, searchPattern, recursionLevel + 1, initialSyncMessageContext: initialSyncMessageContext);
                    await subDirFileInfos.ForEachAsync(async subDirFileInfo => 
                    {
                        await yield.ReturnAsync(subDirFileInfo);
                    });
                }   //foreach (var dirInfo in dirInfos)
#endif
            });   //return new AsyncEnumerable<int>(async yield => {
        }   //private static IEnumerable<FileInfo> ProcessSubDirs(DirectoryInfo srcDirInfo, string searchPattern, bool forHistory, int recursionLevel = 0)

        private static async Task WriteException(Exception ex_in)
        {
            var ex = ex_in;


            if (ex is TaskCanceledException && Global.CancellationToken.IsCancellationRequested)
                return;


            if (ex is AggregateException aggex)
            {
                await WriteException(aggex.InnerException);
                foreach (var aggexInner in aggex.InnerExceptions)
                {
                    await WriteException(aggexInner);
                }
                return;
            }



            ex = ex_in;     //TODO: refactor to shared function

            var message = new StringBuilder();
            message.Append(DateTime.Now);
            message.AppendLine(" Unhandled exception: ");

            message.AppendLine(ex.GetType().ToString());
            message.AppendLine(ex.Message);
            message.AppendLine("Stack Trace:");
            message.AppendLine(ex.StackTrace);

            while (ex.InnerException != null)
            {
                message.AppendLine("");
                message.Append("Inner exception: ");
                message.Append(ex.GetType().ToString());
                message.AppendLine(": ");
                message.AppendLine(ex.InnerException.Message);
                message.AppendLine("Inner exception stacktrace: ");
                message.AppendLine(ex.InnerException.StackTrace);

                ex = ex.InnerException;     //loop
            }

            message.AppendLine("");


            using (await ConsoleWatch.Lock.LockAsync(Global.CancellationToken.Token))
            {
                await FileExtensions.AppendAllTextAsync
                (
                    "UnhandledExceptions.log",
                    message.ToString(),
                    Global.CancellationToken.Token
                );
            }
            

            //Console.WriteLine(ex.Message);
            message.Clear();     //TODO: refactor to shared function
            message.Append(ex.Message.ToString());
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
                //Console.WriteLine(ex.Message);
                message.AppendLine("");
                message.Append(ex.Message);
            }


            var time = DateTime.Now;
            var msg = message.ToString();
            await AddMessage(ConsoleColor.Red, msg, time, showAlert: true, addTimestamp: true);
        }

        private static async Task AddMessage(ConsoleColor color, string message, DateTime time, bool showAlert = false, bool addTimestamp = false, CancellationToken? token = null, bool suppressLogFile = false)
        {
            if (addTimestamp || Global.AddTimestampToNormalLogEntries)
            {
                message = $"[{time:yyyy.MM.dd HH:mm:ss.ffff}] : {message}";
            }


            //await Task.Run(() => 
            {
                using (await ConsoleWatch.Lock.LockAsync(Global.CancellationToken.Token))
                {
                    if (Global.LogToFile && !suppressLogFile)
                    {
                        await FileExtensions.AppendAllTextAsync
                        (
                            "Console.log",
                            message,
                            token ?? Global.CancellationToken.Token
                        );
                    }


                    try
                    {
                        Console.ForegroundColor = color;
                        Console.WriteLine(message);

                        if (
                            showAlert
                            && Global.ShowErrorAlerts
                            && (ConsoleWatch.PrevAlertTime != time || ConsoleWatch.PrevAlertMessage != message)
                        )
                        {
                            MessageBox.Show(message, "AsyncToSyncCodeRoundtripSynchroniserMonitor");
                        }
                    }
                    catch (Exception e)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(e.Message);
                    }
                    finally
                    {
                        Console.ForegroundColor = ConsoleWatch._consoleColor;
                    }
                }
            }//)
            //.WaitAsync(Global.CancellationToken.Token);
        }

        private static Task WaitForCtrlC()
        {
            var exitEvent = new AsyncManualResetEvent(false);
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                Global.CancellationToken.Cancel();
                e.Cancel = true;
                Console.WriteLine("Stop detected.");
                exitEvent.Set();
            };
            return exitEvent.WaitAsync();
        }
    }

    internal class FileInfoRef
    {
        public FileInfo Value;
        public CancellationToken Token;

        [DebuggerStepThrough]
        public FileInfoRef(FileInfo value, CancellationToken token)
        {
            Value = value;
            Token = token;
        }
    }

    internal class Context
    {
        public readonly IFileSystemEvent Event;
        public readonly CancellationToken Token;
        public readonly bool IsSyncPath;
        public readonly bool IsInitialScan;

        public FileSystemInfo FileInfo;
        public bool FileInfoRefreshed;

        public FileInfo OtherFileInfo;

        public DateTime Time
        {
            [DebuggerStepThrough]
            get
            {
                return Event?.DateTimeUtc ?? DateTime.UtcNow;
            }
        }

        [DebuggerStepThrough]
#pragma warning disable CA1068  //should take CancellationToken as the last parameter
        public Context(IFileSystemEvent eventObj, CancellationToken token, bool isSyncPath, bool isInitialScan)
#pragma warning restore CA1068
        {
            Event = eventObj;
            Token = token;
            IsSyncPath = isSyncPath;
            IsInitialScan = isInitialScan;

            FileInfo = eventObj.FileSystemInfo;

            //FileInfo type is a file from directory scan and has stale file length. 
            //NB! if FileInfo is null then it is okay to set FileInfoRefreshed = true since if will be populated later with up-to-date information
            FileInfoRefreshed = !(FileInfo is FileInfo);
        }
    }

    internal class ConsoleWatch
    {
        /// <summary>
        /// The original console color
        /// </summary>
        internal static readonly ConsoleColor _consoleColor = Console.ForegroundColor;

        /// <summary>
        /// We need a static lock so it is shared by all.
        /// </summary>
        internal static readonly AsyncLock Lock = new AsyncLock();

        internal static DateTime PrevAlertTime;
        internal static string PrevAlertMessage;

        private static readonly ConcurrentDictionary<string, DateTime> BidirectionalConverterSavedFileDates = new ConcurrentDictionary<string, DateTime>();
        private static readonly AsyncLockQueueDictionary<string> FileEventLocks = new AsyncLockQueueDictionary<string>();


#pragma warning disable S1118   //Warning	S1118	Hide this public constructor by making it 'protected'.
        public ConsoleWatch(IWatcher3 watch)
#pragma warning restore S1118
        {
            //_consoleColor = Console.ForegroundColor;

            //watch.OnErrorAsync += OnErrorAsync;
            watch.OnAddedAsync += (fse, token) => OnAddedAsync(fse, token, isInitialScan: false);
            watch.OnRemovedAsync += OnRemovedAsync;
            watch.OnRenamedAsync += OnRenamedAsync;
            watch.OnTouchedAsync += OnTouchedAsync;
        }

#if false
        private async Task OnErrorAsync(IEventError ee, CancellationToken token)
        {
            try
            {
                await AddMessage(ConsoleColor.Red, $"[!]:{ee.Message}", context);
            }
            catch (Exception ex)
            {
                await WriteException(ex, context);
            }
        }
#endif

        public static async Task WriteException(Exception ex_in, Context context)
        {
            var ex = ex_in;

            
            //if (ConsoleWatch.DoingInitialSync)  //TODO: config
            //    return;


            if (ex is TaskCanceledException && Global.CancellationToken.IsCancellationRequested)
                return;


            if (ex is AggregateException aggex)
            {
                await WriteException(aggex.InnerException, context);
                foreach (var aggexInner in aggex.InnerExceptions)
                {
                    await WriteException(aggexInner, context);
                }
                return;
            }



            ex = ex_in;     //TODO: refactor to shared function

            var message = new StringBuilder();
            message.Append(DateTime.Now);
            message.AppendLine(" Unhandled exception: ");

            message.AppendLine(ex.GetType().ToString());
            message.AppendLine(ex.Message);
            message.AppendLine("Stack Trace:");
            message.AppendLine(ex.StackTrace);

            while (ex.InnerException != null)
            {
                message.AppendLine("");
                message.Append("Inner exception: ");
                message.Append(ex.GetType().ToString());
                message.AppendLine(": ");
                message.AppendLine(ex.InnerException.Message);
                message.AppendLine("Inner exception stacktrace: ");
                message.AppendLine(ex.InnerException.StackTrace);

                ex = ex.InnerException;     //loop
            }

            message.AppendLine("");


            using (await ConsoleWatch.Lock.LockAsync(context.Token))
            {
                await FileExtensions.AppendAllTextAsync
                (
                    "UnhandledExceptions.log",
                    message.ToString(),
                    context.Token
                );
            }


            //Console.WriteLine(ex.Message);
            message.Clear();     //TODO: refactor to shared function
            message.Append(ex.Message.ToString());
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
                //Console.WriteLine(ex.Message);
                message.AppendLine("");
                message.Append(ex.Message);
            }


            var msg = $"{context.Event?.FullName} : {message}";
            await AddMessage(ConsoleColor.Red, msg, context, showAlert: true, addTimestamp: true);
        }

        public static bool IsAsyncPath(string fullNameInvariant)
        {
            return Extensions.GetLongPath(fullNameInvariant).StartsWith(Extensions.GetLongPath(Global.AsyncPath));
        }

        public static bool IsSyncPath(string fullNameInvariant)
        {
            return Extensions.GetLongPath(fullNameInvariant).StartsWith(Extensions.GetLongPath(Global.SyncPath));
        }

        public static string GetNonFullName(string fullName)
        {
            var fullNameInvariant = fullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);

            if (IsAsyncPath(fullNameInvariant))
            {
                return fullName.Substring(Extensions.GetLongPath(Global.AsyncPath).Length);
            }
            else if (IsSyncPath(fullNameInvariant))
            {
                return fullName.Substring(Extensions.GetLongPath(Global.SyncPath).Length);
            }
            else
            {
                throw new ArgumentException("fullName");
            }
        }

        public static string GetOtherFullName(Context context)
        {
            var fullNameInvariant = context.Event.FullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);
            var nonFullName = GetNonFullName(context.Event.FullName);

            if (IsAsyncPath(fullNameInvariant))
            {
                return Path.Combine(Global.SyncPath, nonFullName);
            }
            else if (IsSyncPath(fullNameInvariant))
            {
                return Path.Combine(Global.AsyncPath, nonFullName);
            }
            else
            {
                throw new ArgumentException("fullName");
            }
        }

        public static async Task DeleteFile(FileInfoRef otherFileInfo, string otherFullName, Context context)
        {
            try
            {
                otherFullName = Extensions.GetLongPath(otherFullName);

                while (true)
                {
                    context.Token.ThrowIfCancellationRequested();

                    try
                    {
                        var backupFileInfo = new FileInfoRef(null, context.Token);
                        if (await GetFileExists(backupFileInfo, otherFullName + "~"))
                        {
#pragma warning disable SEC0116 //Warning	SEC0116	Unvalidated file paths are passed to a file delete API, which can allow unauthorized file system operations (e.g. read, write, delete) to be performed on unintended server files.
                            await Extensions.FSOperation(() => File.Delete(otherFullName + "~"), context.Token);
#pragma warning restore SEC0116
                        }

                        //fileInfo?.Refresh();
                        if (await GetFileExists(otherFileInfo, otherFullName))
                        {
                            await Extensions.FSOperation(() => File.Move(otherFullName, otherFullName + "~"), context.Token);
                        }

                        return;
                    }
                    catch (IOException)
                    {
                        //retry after delay
#if !NOASYNC
                        await Task.Delay(1000, context.Token);     //TODO: config file?
#else
                        context.Token.WaitHandle.WaitOne(1000);
#endif
                    }
                }
            }
            catch (Exception ex)
            {
                await WriteException(ex, context);
            }
        }   //public static async Task DeleteFile(string fullName, Context context)

        public static DateTime GetBidirectionalConverterSaveDate(string fullName)
        {
            DateTime converterSaveDate;
            if (!BidirectionalConverterSavedFileDates.TryGetValue(fullName, out converterSaveDate))
            {
                converterSaveDate = DateTime.MinValue;
            }

            return converterSaveDate;
        }

        public static async Task RefreshFileInfo(Context context)
        {
            var fileInfo = context.Event.FileSystemInfo as FileInfo;
            if (fileInfo != null && !context.FileInfoRefreshed)
            {
                context.FileInfoRefreshed = true;

                await Extensions.FSOperation
                (
                    () => 
                    {
                        fileInfo.Refresh();    //https://stackoverflow.com/questions/7828132/getting-current-file-length-fileinfo-length-caching-and-stale-information
                        if (fileInfo.Exists)
                        {
                            var dummyAttributes = fileInfo.Attributes;
                            var dymmyLength = fileInfo.Length;
                            var dymmyTime = fileInfo.LastWriteTimeUtc;
                        }
                    },
                    context.Token
                );
            }
        }

        public static async Task<bool> NeedsUpdate(Context context)
        {
            if (context.IsInitialScan)
            {
                return true;
            }


            var fileInfoForLength = context.FileInfo as FileInfo;
            if (fileInfoForLength != null)   //a file from directory scan
            {
                //await RefreshFileInfo(context);

                var fileLength = fileInfoForLength.Length;      //NB! this info might be stale, but lets ignore that issue here
                long maxFileSize = Math.Min(FileExtensions.MaxByteArraySize, Global.MaxFileSizeMB * (1024 * 1024));
                if (maxFileSize > 0 && fileLength > maxFileSize)
                {
                    await AddMessage(ConsoleColor.Red, $"Error synchronising updates from file {context.Event.FullName} : fileLength > maxFileSize : {fileLength} > {maxFileSize}", context);

                    return false;
                }
            }

            var converterSaveDate = GetBidirectionalConverterSaveDate(context.Event.FullName);
            var fileTime = context.Event.FileSystemInfo.LastWriteTimeUtc; //GetFileTime(context.Event.FullName);

            if (
                !Global.Bidirectional   //no need to debounce BIDIRECTIONAL file save events when bidirectional save is disabled
                || fileTime > converterSaveDate.AddSeconds(3)     //NB! ignore if the file changed during 3 seconds after converter save   //TODO!! config
            )
            {
                var otherFullName = GetOtherFullName(context);

                var otherFileInfoRef = new FileInfoRef(context.OtherFileInfo, context.Token);
                var otherFileTime = await GetFileTime(otherFileInfoRef, otherFullName);
                context.OtherFileInfo = otherFileInfoRef.Value;

                if (fileTime > otherFileTime)     //NB!
                {
                    return true;
                }
            }

            return false;
        }

        public static async Task FileUpdated(Context context)
        {
            if (
                IsWatchedFile(context.Event.FullName)
                && (await NeedsUpdate(context))     //NB!
            )
            {
                var otherFullName = GetOtherFullName(context);
                using (await Global.FileOperationLocks.LockAsync(context.Event.FullName, otherFullName, context.Token))
                {
                    using (await Global.FileOperationSemaphore.LockAsync())
                    {
                        var fullNameInvariant = context.Event.FullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);

                        if (
                            Global.WatchedCodeExtension.Any(x => fullNameInvariant.EndsWith("." + x))
                            || Global.WatchedCodeExtension.Contains("*")
                        )
                        {
                            if (IsAsyncPath(fullNameInvariant))
                            {
                                await AsyncToSyncConverter.AsyncFileUpdated(context);
                            }
                            else if (IsSyncPath(fullNameInvariant))     //NB!
                            {
                                await SyncToAsyncConverter.SyncFileUpdated(context);
                            }
                            else
                            {
                                throw new ArgumentException("fullName");
                            }
                        }
                        else    //Assume ResX file
                        {
                            long maxFileSize = Math.Min(FileExtensions.MaxByteArraySize, Global.MaxFileSizeMB * (1024 * 1024));
                            //TODO: consider destination disk free space here together with the file size already before reading the file

                            Tuple<byte[], long> fileDataTuple = null;
                            try
                            { 
                                fileDataTuple = await FileExtensions.ReadAllBytesAsync(Extensions.GetLongPath(context.Event.FullName), context.Token, retryCount: Global.RetryCountOnSrcFileOpenError);
                                if (fileDataTuple.Item1 == null)   //maximum length exceeded
                                {
                                    if (fileDataTuple.Item2 >= 0)
                                        await AddMessage(ConsoleColor.Red, $"Error synchronising updates from file {context.Event.FullName} : fileLength > maxFileSize : {fileDataTuple.Item2} > {maxFileSize}", context);
                                    else
                                        await AddMessage(ConsoleColor.Red, $"Error synchronising updates from file {context.Event.FullName}", context);

                                    return; //TODO: log error?
                                }                            
                            }
                            catch (FileNotFoundException)   //file was removed by the time queue processing got to it
                            {
                                return;     
                            }

                            //save without transformations
                            await ConsoleWatch.SaveFileModifications(fileDataTuple.Item1, context);
                        }
                    }   //using (await Global.FileOperationSemaphore.LockAsync())
                }   //using (await Global.FileOperationLocks.LockAsync(fullName, otherFullName, context.Token))
            }
        }   //public static async Task FileUpdated(string fullName, Context context)

        private static async Task FileDeleted(Context context)
        {
            if (IsWatchedFile(context.Event.FullName))
            {
                await RefreshFileInfo(context);  //NB! verify that the file is still deleted

                if (!await GetFileExists(context))  //NB! verify that the file is still deleted
                {
                    var otherFullName = GetOtherFullName(context);

                    var otherFileInfo = new FileInfoRef(null, context.Token);
                    await DeleteFile(otherFileInfo, otherFullName, context);
                }
                else    //NB! file appears to be recreated
                {
                    //await FileUpdated(fullName, context);
                }
            }
        }

        private static async Task<FileInfo> GetFileInfo(string fullName, CancellationToken token)
        {
            fullName = Extensions.GetLongPath(fullName);

            var result = await Extensions.FSOperation
            (
                () => 
                {
                    var fileInfo = new FileInfo(fullName);

                    //this will cause the actual filesystem call
                    if (fileInfo.Exists)
                    {
                        var dummyAttributes = fileInfo.Attributes;
                        var dymmyLength = fileInfo.Length;
                        var dymmyTime = fileInfo.LastWriteTimeUtc;
                    }

                    return fileInfo;
                },
                token
            ); 

            return result;
        }

        private static async Task<bool> GetIsFile(Context context)
        {
            if (context.FileInfo == null)
            {
                context.FileInfo = await GetFileInfo(context.Event.FullName, context.Token);
            }

            return (context.FileInfo.Attributes & FileAttributes.Directory) == 0;
        }

        private static async Task<bool> GetFileExists(Context context)
        {
            if (context.FileInfo == null)
            {
                context.FileInfo = await GetFileInfo(context.Event.FullName, context.Token);
            }

            return context.FileInfo.Exists && (context.FileInfo.Attributes & FileAttributes.Directory) == 0;
        }

        private static async Task<bool> GetFileExists(FileInfoRef fileInfo, string fullName)
        {
            if (fileInfo.Value == null)
            {
                fileInfo.Value = await GetFileInfo(fullName, fileInfo.Token);
            }

            return fileInfo.Value.Exists && (fileInfo.Value.Attributes & FileAttributes.Directory) == 0;
        }

        private static async Task<DateTime> GetFileTime(FileInfoRef otherFileInfo, string otherFullName)
        {
            if (otherFileInfo.Value == null)
            {
                otherFileInfo.Value = await GetFileInfo(otherFullName, otherFileInfo.Token);

                if (!await GetFileExists(otherFileInfo, otherFullName))
                {
                    return DateTime.MinValue;
                }
            }

            return otherFileInfo.Value.LastWriteTimeUtc;
        }

        private static async Task<long> GetFileSize(FileInfoRef otherFileInfo, string otherFullName)
        {
            if (otherFileInfo.Value == null)
            {
                otherFileInfo.Value = await GetFileInfo(otherFullName, otherFileInfo.Token);

                if (!await GetFileExists(otherFileInfo, otherFullName))
                {
                    return -1;
                }
            }

            //NB! no RefreshFileInfo or GetFileExists calls here

            return otherFileInfo.Value.Length;
        }

        private static bool IsWatchedFile(string fullName)
        {
            var fullNameInvariant = fullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);

            if (
                (
                    Global.WatchedCodeExtension.Any(x => fullNameInvariant.EndsWith("." + x))
                    || Global.WatchedResXExtension.Any(x => fullNameInvariant.EndsWith("." + x))
                    || Global.WatchedCodeExtension.Contains("*")
                    || Global.WatchedResXExtension.Contains("*")
                )
                &&
                Global.ExcludedExtensions.All(x =>  //TODO: optimise

                    !fullNameInvariant.EndsWith("." + x)
                    &&
                    (   //handle exclusion patterns in the forms like *xyz
                        !x.StartsWith("*")
                        || fullNameInvariant.Length < x.Length - 1
                        || !fullNameInvariant.EndsWith(/*"." + */x.Substring(1))    //NB! the existence of dot is not verified in this case     //TODO: use Regex
                    )
                )
            )
            {
                var nonFullNameInvariantWithLeadingSlash = Program.DirectorySeparatorChar + GetNonFullName(fullNameInvariant);

                if (
                    //Global.IgnorePathsStartingWith.Any(x => nonFullNameInvariantWithLeadingSlash.StartsWith(x))
                    //|| Global.IgnorePathsContaining.Any(x => nonFullNameInvariantWithLeadingSlash.Contains(x))
                    //|| Global.IgnorePathsEndingWith.Any(x => nonFullNameInvariantWithLeadingSlash.EndsWith(x))
                    Global.IgnorePathsContainingACHasAny  //needed to avoid exceptions
                    && Global.IgnorePathsContainingAC.ParseText(Program.NullChar + nonFullNameInvariantWithLeadingSlash + Program.NullChar).Any()
                )
                {
                    return false;
                }

                return true;
            }

            return false;

        }   //private bool IsWatchedFile(string fullName)

#pragma warning disable AsyncFixer01
        private static async Task OnRenamedAsync(IRenamedFileSystemEvent fse, CancellationToken token)
        {
            //NB! create separate context to properly handle disk free space checks on cases where file is renamed from src path to dest path (not a recommended practice though!)

            var prevFileFSE = new DummyFileSystemEvent(fse.PreviousFileSystemInfo);
            var previousFullNameInvariant = prevFileFSE.FullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);
            var previousContext = new Context(prevFileFSE, token, isSyncPath: IsSyncPath(previousFullNameInvariant), isInitialScan: false);

            var newFullNameInvariant = fse.FileSystemInfo.FullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);
            var newContext = new Context(fse, token, isSyncPath: IsSyncPath(newFullNameInvariant), isInitialScan: false);

            try
            {
                if (fse.IsFile)
                {
                    var prevFileIsWatchedFile = IsWatchedFile(fse.PreviousFileSystemInfo.FullName);
                    var newFileIsWatchedFile = IsWatchedFile(fse.FileSystemInfo.FullName);

                    if (prevFileIsWatchedFile
                        || newFileIsWatchedFile)
                    {
                        await AddMessage(ConsoleColor.Cyan, $"[{(fse.IsFile ? "F" : "D")}][R]:{fse.PreviousFileSystemInfo.FullName} > {fse.FileSystemInfo.FullName}", newContext);

                        //NB! if file is renamed to cs~ or resx~ then that means there will be yet another write to same file, so lets skip this event here - NB! skip the event here, including delete event of the previous file
                        if (!fse.FileSystemInfo.FullName.EndsWith("~"))
                        {
                            //using (await Global.FileOperationLocks.LockAsync(rfse.FileSystemInfo.FullName, rfse.PreviousFileSystemInfo.FullName, context.Token))  //comment-out: prevent deadlock
                            {
                                if (newFileIsWatchedFile)
                                {
                                    using (await FileEventLocks.LockAsync(fse.FileSystemInfo.FullName, token))
                                    {
                                        await FileUpdated(newContext);
                                    }
                                }

                                if (prevFileIsWatchedFile)
                                {
                                    if (
                                        newFileIsWatchedFile     //both files were watched files
                                        && previousContext.IsSyncPath != newContext.IsSyncPath
                                        &&
                                        (
                                            Global.Bidirectional      //move in either direction between sync and async
                                            || previousContext.IsSyncPath   //sync -> async move
                                        )
                                    )
                                    {
                                        //the file was moved from one watched path to another watched path, which is illegal, lets ignore the file move

                                        await AddMessage(ConsoleColor.Red, $"Ignoring file delete in the source path since the move was to the other managed path : {fse.PreviousFileSystemInfo.FullName} > {fse.FileSystemInfo.FullName}", previousContext);
                                    }
                                    else
                                    {
                                        using (await FileEventLocks.LockAsync(prevFileFSE.FileSystemInfo.FullName, token))
                                        {
                                            await FileDeleted(previousContext);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    await AddMessage(ConsoleColor.Cyan, $"[{(fse.IsFile ? "F" : "D")}][R]:{fse.PreviousFileSystemInfo.FullName} > {fse.FileSystemInfo.FullName}", newContext);

                    //TODO trigger update / delete event for all files in new folder
                }
            }
            catch (Exception ex)
            {
                await WriteException(ex, newContext);
            }
        }   //private static async Task OnRenamedAsync(IRenamedFileSystemEvent fse, CancellationToken token)

        private static async Task OnRemovedAsync(IFileSystemEvent fse, CancellationToken token)
        {
            var fullNameInvariant = fse.FileSystemInfo.FullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);
            var context = new Context(fse, token, isSyncPath: IsSyncPath(fullNameInvariant), isInitialScan: false);

            try
            {
                if (fse.IsFile)
                {
                    if (IsWatchedFile(fse.FileSystemInfo.FullName))
                    {
                        await AddMessage(ConsoleColor.Yellow, $"[{(fse.IsFile ? "F" : "D")}][-]:{fse.FileSystemInfo.FullName}", context);

                        using (await FileEventLocks.LockAsync(fse.FileSystemInfo.FullName, token))
                        {
                            await FileDeleted(context);
                        }
                    }
                }
                else
                {
                    //nothing to do here: the files are likely already deleted by now
                }
            }
            catch (Exception ex)
            {
                await WriteException(ex, context);
            }
        }

        internal static async Task OnAddedAsync(IFileSystemEvent fse, CancellationToken token, bool isInitialScan)
        {
            var fullNameInvariant = fse.FileSystemInfo.FullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);
            var context = new Context(fse, token, isSyncPath: IsSyncPath(fullNameInvariant), isInitialScan: isInitialScan);

            try
            {
                if (fse.IsFile)
                {
                    if (IsWatchedFile(fse.FileSystemInfo.FullName))
                    {
                        if (!context.IsInitialScan)
                           await AddMessage(ConsoleColor.Green, $"[{(fse.IsFile ? "F" : "D")}][+]:{fse.FileSystemInfo.FullName}", context);

                        using (await FileEventLocks.LockAsync(fse.FileSystemInfo.FullName, token))
                        {
                            await FileUpdated(context);
                        }
                    }
                }
                else
                {
                    //nothing to do here: there are likely no files in here yet
                }
            }
            catch (Exception ex)
            {
                await WriteException(ex, context);
            }
        }

        private static async Task OnTouchedAsync(IFileSystemEvent fse, CancellationToken token)
        {
            var fullNameInvariant = fse.FileSystemInfo.FullName.ToUpperInvariantOnWindows(Global.CaseSensitiveFilenames);
            var context = new Context(fse, token, isSyncPath: IsSyncPath(fullNameInvariant), isInitialScan: false);

            try
            {
                if (fse.IsFile)
                {
                    if (IsWatchedFile(fse.FileSystemInfo.FullName))
                    {
                        //check for file type only after checking IsWatchedFile first since file type checking might already be a slow operation
                        if (await GetIsFile(context))     //for some reason fse.IsFile is set even for folders
                        {
                            await AddMessage(ConsoleColor.Gray, $"[{(fse.IsFile ? "F" : "D")}][T]:{fse.FileSystemInfo.FullName}", context);

                            using (await FileEventLocks.LockAsync(fse.FileSystemInfo.FullName, token))
                            {
                                await FileUpdated(context);
                            }
                        }
                    }
                }
                else
                {
                    //nothing to do here: file update events are sent separately anyway
                }
            }
            catch (Exception ex)
            {
                await WriteException(ex, context);
            }
        }

        public static async Task AddMessage(ConsoleColor color, string message, Context context, bool showAlert = false, bool addTimestamp = false)
        {
            if (addTimestamp || Global.AddTimestampToNormalLogEntries)
            {
                var time = context.Time.ToLocalTime();
                message = $"[{time:yyyy.MM.dd HH:mm:ss.ffff}] : {message}";
            }


            //await Task.Run(() =>
            {
                using (await ConsoleWatch.Lock.LockAsync(context.Token))
                {
                    if (Global.LogToFile)
                    {
                        await FileExtensions.AppendAllTextAsync
                        (
                            "Console.log",
                            message,
                            context.Token
                        );
                    }


                    try
                    {
                        Console.ForegroundColor = color;
                        Console.WriteLine(message);

                        if (
                            showAlert
                            && Global.ShowErrorAlerts
                            && (PrevAlertTime != context.Time || PrevAlertMessage != message)
                        )
                        {
                            PrevAlertTime = context.Time;
                            PrevAlertMessage = message;

                            MessageBox.Show(message, "AsyncToSyncCodeRoundtripSynchroniserMonitor");
                        }
                    }
                    catch (Exception e)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(e.Message);
                    }
                    finally
                    {
                        Console.ForegroundColor = _consoleColor;
                    }
                }
            }//)
            //.WaitAsync(context.Token);
        }

        public static async Task SaveFileModifications(string fileData, string originalData, Context context)
        {
            //if (fileData == originalData)
            //    return;


            var otherFullName = GetOtherFullName(context);
            var otherFileInfoRef = new FileInfoRef(context.OtherFileInfo, context.Token);
            var otherFileLength = await GetFileSize(otherFileInfoRef, otherFullName);
            context.OtherFileInfo = otherFileInfoRef.Value;


            //NB! detect whether the file actually changed
            var otherFileData =
                (await GetFileExists(otherFileInfoRef, otherFullName))
                    && otherFileLength == fileData.Length   //optimisation
                ? await FileExtensions.ReadAllTextAsync(Extensions.GetLongPath(otherFullName), context.Token)     //TODO: optimisation: no need to read the bytes in case the file lengths are different
                : null;

            if (
                (otherFileData?.Length ?? -1) != fileData.Length
                || otherFileData != fileData
            )
            {
                var minDiskFreeSpace = context.IsSyncPath ? Global.AsyncPathMinFreeSpace : Global.SyncPathMinFreeSpace;
                var actualFreeSpace = minDiskFreeSpace > 0 ? Extensions.CheckDiskSpace(otherFullName) : 0;
                if (minDiskFreeSpace > actualFreeSpace - fileData.Length)
                {
                    await AddMessage(ConsoleColor.Red, $"Error synchronising updates from file {context.Event.FullName} : minDiskFreeSpace > actualFreeSpace : {minDiskFreeSpace} > {actualFreeSpace}", context);

                    return;
                }


                //await DeleteFile(otherFileInfoRef, otherFullName, context);

                var otherDirName = Path.GetDirectoryName(otherFullName);
                if (!await Extensions.FSOperation(() => Directory.Exists(Extensions.GetLongPath(otherDirName)), context.Token))
                    await Extensions.FSOperation(() => Directory.CreateDirectory(Extensions.GetLongPath(otherDirName)), context.Token);

                await FileExtensions.WriteAllTextAsync(Extensions.GetLongPath(otherFullName), fileData, createTempFileFirst: true, cancellationToken: context.Token);

                var now = DateTime.UtcNow;  //NB! compute now after saving the file
                BidirectionalConverterSavedFileDates[otherFullName] = now;


                await AddMessage(ConsoleColor.Magenta, $"Synchronised updates from file {context.Event.FullName}", context);
            }
            else if (false)
            {
                //touch the file
                var now = DateTime.UtcNow;  //NB! compute common now for ConverterSavedFileDates

                try
                {
                    await Extensions.FSOperation(() => File.SetLastWriteTimeUtc(Extensions.GetLongPath(otherFullName), now), context.Token);
                }
                catch (Exception ex)
                {
                    await ConsoleWatch.WriteException(ex, context);
                }

                BidirectionalConverterSavedFileDates[otherFullName] = now;
            }
        }   //public static async Task SaveFileModifications(string fullName, string fileData, string originalData, Context context)

        public static async Task SaveFileModifications(byte[] fileData, Context context)
        {
            var otherFullName = GetOtherFullName(context);
            var otherFileInfoRef = new FileInfoRef(context.OtherFileInfo, context.Token);
            var otherFileLength = await GetFileSize(otherFileInfoRef, otherFullName);
            context.OtherFileInfo = otherFileInfoRef.Value;


            //NB! detect whether the file actually changed
            var otherFileDataTuple =
                (await GetFileExists(otherFileInfoRef, otherFullName))
                    && otherFileLength == fileData.Length   //optimisation
                ? await FileExtensions.ReadAllBytesAsync(Extensions.GetLongPath(otherFullName), context.Token)    //TODO: optimisation: no need to read the bytes in case the file lengths are different
                : null;

            if (
                (otherFileDataTuple?.Item1?.Length ?? -1) != fileData.Length
                || !FileExtensions.BinaryEqual(otherFileDataTuple.Item1, fileData)
            )
            {
                var minDiskFreeSpace = context.IsSyncPath ? Global.AsyncPathMinFreeSpace : Global.SyncPathMinFreeSpace;
                var actualFreeSpace = minDiskFreeSpace > 0 ? Extensions.CheckDiskSpace(otherFullName) : 0;
                if (minDiskFreeSpace > actualFreeSpace - fileData.Length)
                {
                    await AddMessage(ConsoleColor.Red, $"Error synchronising updates from file {context.Event.FullName} : minDiskFreeSpace > actualFreeSpace : {minDiskFreeSpace} > {actualFreeSpace}", context);

                    return;
                }


                //await DeleteFile(otherFileInfoRef, otherFullName, context);

                var otherDirName = Path.GetDirectoryName(otherFullName);
                if (!await Extensions.FSOperation(() => Directory.Exists(Extensions.GetLongPath(otherDirName)), context.Token))
                    await Extensions.FSOperation(() => Directory.CreateDirectory(Extensions.GetLongPath(otherDirName)), context.Token);

                await FileExtensions.WriteAllBytesAsync(Extensions.GetLongPath(otherFullName), fileData, createTempFileFirst: true, cancellationToken: context.Token);

                var now = DateTime.UtcNow;  //NB! compute now after saving the file
                BidirectionalConverterSavedFileDates[otherFullName] = now;


                await AddMessage(ConsoleColor.Magenta, $"Synchronised updates from file {context.Event.FullName}", context);
            }
            else if (false)     //TODO: config
            {
                //touch the file
                var now = DateTime.UtcNow;  //NB! compute common now for ConverterSavedFileDates

                try
                {
                    await Extensions.FSOperation(() => File.SetLastWriteTimeUtc(Extensions.GetLongPath(otherFullName), now), context.Token);
                }
                catch (Exception ex)
                {
                    await ConsoleWatch.WriteException(ex, context);
                }

                BidirectionalConverterSavedFileDates[otherFullName] = now;
            }
        }   //public static async Task SaveFileModifications(string fullName, byte[] fileData, byte[] originalData, Context context)

#pragma warning restore AsyncFixer01
    }

    internal static class WindowsDllImport  //keep in a separate class just in case to ensure that dllimport is not attempted during application loading under non-Windows OS
    {
        //https://stackoverflow.com/questions/61037184/find-out-free-and-total-space-on-a-network-unc-path-in-netcore-3-x
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
            out long lpFreeBytesAvailable,
            out long lpTotalNumberOfBytes,
            out long lpTotalNumberOfFreeBytes);


        public enum PROCESSINFOCLASS : int
        {
            ProcessIoPriority = 33
        };

        public enum PROCESSIOPRIORITY : int
        {
            PROCESSIOPRIORITY_VERY_LOW = 0,
            PROCESSIOPRIORITY_LOW,
            PROCESSIOPRIORITY_NORMAL,
            PROCESSIOPRIORITY_HIGH
        };

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtSetInformationProcess(IntPtr processHandle,
            PROCESSINFOCLASS processInformationClass, 
            [In] ref int processInformation,
            uint processInformationLength);

        public static bool NT_SUCCESS(int Status)
        {
            return (Status >= 0);
        }

        public static bool SetIOPriority(IntPtr processHandle, PROCESSIOPRIORITY ioPriorityIn)
        {
            //PROCESSINFOCLASS.ProcessIoPriority is actually only available only on XPSP3, Server2003, Vista or newer: http://blogs.norman.com/2011/security-research/ntqueryinformationprocess-ntsetinformationprocess-cheat-sheet
            try
            {
                int ioPriority = (int)ioPriorityIn;
                int result = NtSetInformationProcess(processHandle, PROCESSINFOCLASS.ProcessIoPriority, ref ioPriority, sizeof(int));
                return NT_SUCCESS(result);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }   //internal static class WindowsDllImport
}
