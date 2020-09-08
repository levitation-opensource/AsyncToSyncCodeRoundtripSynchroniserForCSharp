//
// Copyright (c) Roland Pihlakas 2019 - 2020
// roland@simplify.ee
//
// Roland Pihlakas licenses this file to you under the GNU Lesser General Public License, ver 2.1.
// See the LICENSE file for more information.
//

#define ASYNC
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using myoddweb.directorywatcher;
using myoddweb.directorywatcher.interfaces;

namespace AsyncToSyncCodeRoundtripSynchroniserMonitor
{
#pragma warning disable S2223   //Warning	S2223	Change the visibility of 'xxx' or make it 'const' or 'readonly'.
    internal static class Global
    {
        public static IConfigurationRoot Configuration;

        public static List<string> WatchedCodeExtension = new List<string>() { "cs" };
        public static List<string> WatchedResXExtension = new List<string>() { "resx" };

        public static List<string> ExcludedExtensions = new List<string>() { "*~", "tmp" };
        public static List<string> IgnorePathsStartingWith = new List<string>();
        public static List<string> IgnorePathsContaining = new List<string>();
        
        public static string AsyncPath = "";
        public static string SyncPath = "";

        public static bool Bidirectional = true;



        internal static readonly AsyncLockQueueDictionary FileOperationLocks = new AsyncLockQueueDictionary();
        //internal static readonly AsyncLock FileOperationAsyncLock = new AsyncLock();
    }
#pragma warning restore S2223

    internal class Program
    {
        private class DummyFileSystemEvent : IFileSystemEvent
        {
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

            public FileSystemInfo FileSystemInfo { get; }
            public string FullName { get; }
            public string Name { get; }
            public EventAction Action { get; }
            public EventError Error { get; }
            public DateTime DateTimeUtc { get; }
            public bool IsFile { get; }

            public bool Is(EventAction action)
            {
                return action == Action;
            }
        }

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

            Global.Bidirectional = fileConfig["Bidirectional"]?.ToUpperInvariant() != "FALSE";   //default is true

            Global.AsyncPath = fileConfig["AsyncPath"];
            Global.SyncPath = fileConfig["SyncPath"];

            Global.WatchedCodeExtension = fileConfig.GetSection("WatchedCodeExtension").GetChildren().Select(c => c.Value.ToUpperInvariant()).ToList();
            Global.WatchedResXExtension = fileConfig.GetSection("WatchedResXExtension").GetChildren().Select(c => c.Value.ToUpperInvariant()).ToList();

            //this would need Microsoft.Extensions.Configuration and Microsoft.Extensions.Configuration.Binder packages
            //Global.ExcludedExtensions = fileConfig.GetSection("ExcludedExtensions").Get<string[]>();
            Global.ExcludedExtensions = fileConfig.GetSection("ExcludedExtensions").GetChildren().Select(c => c.Value.ToUpperInvariant()).ToList();   //NB! .ToUpperInvariant()

            Global.IgnorePathsStartingWith = fileConfig.GetSection("IgnorePathsStartingWith").GetChildren().Select(c => c.Value.ToUpperInvariant()).ToList();   //NB! .ToUpperInvariant()
            Global.IgnorePathsContaining = fileConfig.GetSection("IgnorePathsContaining").GetChildren().Select(c => c.Value.ToUpperInvariant()).ToList();   //NB! .ToUpperInvariant()


            var pathHashes = "";
            pathHashes += "_" + GetHashString(Global.AsyncPath.ToUpperInvariant());
            pathHashes += "_" + GetHashString(Global.SyncPath.ToUpperInvariant());

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

                //start the monitor.
                using (var watch = new Watcher())
                {
                    //var drvs = System.IO.DriveInfo.GetDrives();
                    //foreach (var drv in drvs)
                    //{
                    //    if (drv.DriveType == System.IO.DriveType.Fixed)
                    //    {
                    //        watch.Add(new Request(drv.Name, true));
                    //    }
                    //}

                    watch.Add(new Request(Global.SyncPath, recursive: true));

                    if (Global.Bidirectional)
                    {
                        watch.Add(new Request(Global.AsyncPath, recursive: true));
                    }


                    // prepare the console watcher so we can output pretty messages.
                    var consoleWatch = new ConsoleWatch(watch);


                    //start watching
                    //NB! start watching before synchronisation
                    watch.Start();


                    var messageContext = new Context(
                        eventObj: null,
                        token: new CancellationToken()
                    );


                    await ConsoleWatch.AddMessage(ConsoleColor.White, "Doing initial synchronisation...", messageContext);
                    ConsoleWatch.DoingInitialSync = true;   //NB!



                    //1. Do initial synchronisation from sync to async folder   //TODO: config for enabling and ordering of this operation
                    foreach (var fileInfo in ProcessSubDirs(new DirectoryInfo(Global.SyncPath), "*.*"))     //NB! use *.* in order to sync resx files also
                    {
                        await ConsoleWatch.OnAddedAsync
                        (
                            new DummyFileSystemEvent(fileInfo),
                            new CancellationToken()
                        );
                    }

                    if (Global.Bidirectional)
                    {
                        //2. Do initial synchronisation from async to sync folder   //TODO: config for enabling and ordering of this operation
                        foreach (var fileInfo in ProcessSubDirs(new DirectoryInfo(Global.AsyncPath), "*.*"))     //NB! use *.* in order to sync resx files also
                        {
                            await ConsoleWatch.OnAddedAsync
                            (
                                new DummyFileSystemEvent(fileInfo),
                                new CancellationToken()
                            );
                        }
                    }



                    ConsoleWatch.DoingInitialSync = false;   //NB!
                    await ConsoleWatch.AddMessage(ConsoleColor.White, "Done initial synchronisation...", messageContext);


                    //listen for the Ctrl+C 
                    WaitForCtrlC();

                    Console.WriteLine("Stopping...");

                    //stop everything.
                    watch.Stop();

                    Console.WriteLine("Exiting...");

                    GC.KeepAlive(consoleWatch);
                }
            }
            catch (Exception ex)
            {
                WriteException(ex);
            }
        }

        private static IEnumerable<FileInfo> ProcessSubDirs(DirectoryInfo srcDirInfo, string searchPattern, int recursionLevel = 0)
        {
#if false //this built-in functio will throw IOException in case some subfolder is an invalid reparse point
            return new DirectoryInfo(sourceDir)
                .GetFiles(searchPattern, SearchOption.AllDirectories);
#else
            FileInfo[] fileInfos;
            try
            {
                fileInfos = srcDirInfo.GetFiles(searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
            {
                //ignore exceptions due to long pathnames       //TODO: find a way to handle them
                fileInfos = Array.Empty<FileInfo>();
            }

            foreach (var fileInfo in fileInfos)
            {
                yield return fileInfo;
            }


            DirectoryInfo[] dirInfos;
#pragma warning disable S2327   //Warning	S2327	Combine this 'try' with the one starting on line XXX.
            try
            {
                dirInfos = srcDirInfo.GetDirectories("*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
            {
                //ignore exceptions due to long pathnames       //TODO: find a way to handle them
                dirInfos = Array.Empty<DirectoryInfo>();
            }
#pragma warning restore S2327

            foreach (var dirInfo in dirInfos)
            {
                //TODO: option to follow reparse points
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    continue;


                var nonFullNameInvariant = ConsoleWatch.GetNonFullName(dirInfo.FullName) + Path.PathSeparator;
                if (
                    Global.IgnorePathsStartingWith.Any(x => nonFullNameInvariant.StartsWith(x))
                    || Global.IgnorePathsContaining.Any(x => nonFullNameInvariant.Contains(x))
                )
                {
                    continue;
                }


                var subDirFileInfos = ProcessSubDirs(dirInfo, searchPattern, recursionLevel + 1);
                foreach (var subDirFileInfo in subDirFileInfos)
                {
                    yield return subDirFileInfo;
                }
            }   //foreach (var dirInfo in dirInfos)
#endif
        }   //private static IEnumerable<FileInfo> ProcessSubDirs(DirectoryInfo srcDirInfo, string searchPattern, bool forHistory, int recursionLevel = 0)
        private static void WriteException(Exception ex)
        {
            if (ex is AggregateException aggex)
            {
                WriteException(aggex.InnerException);
                foreach (var aggexInner in aggex.InnerExceptions)
                {
                    WriteException(aggexInner);
                }
                return;
            }

            Console.WriteLine(ex.Message);
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
                Console.WriteLine(ex.Message);
            }
        }

        private static void WaitForCtrlC()
        {
            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Console.WriteLine("Stop detected.");
                exitEvent.Set();
            };
            exitEvent.WaitOne();
        }
    }

    internal class Context
    {
        public IFileSystemEvent Event;
        public CancellationToken Token;

        public DateTime Time
        {
            get
            {
                return Event?.DateTimeUtc ?? DateTime.UtcNow;
            }
        }

        public Context(IFileSystemEvent eventObj, CancellationToken token)
        {
            Event = eventObj;
            Token = token;
        }
    }

    internal class ConsoleWatch
    {
        /// <summary>
        /// The original console color
        /// </summary>
        private static readonly ConsoleColor _consoleColor = Console.ForegroundColor;

        /// <summary>
        /// We need a static lock so it is shared by all.
        /// </summary>
        private static readonly object Lock = new object();
        //private static readonly AsyncLock AsyncLock = new AsyncLock();  //TODO: use this

#pragma warning disable S2223   //Warning	S2223	Change the visibility of 'DoingInitialSync' or make it 'const' or 'readonly'.
        public static bool DoingInitialSync = false;
#pragma warning restore S2223

        private static ConcurrentDictionary<string, DateTime> BidirectionalConverterSavedFileDates = new ConcurrentDictionary<string, DateTime>();
        private static readonly AsyncLockQueueDictionary FileEventLocks = new AsyncLockQueueDictionary();


        public ConsoleWatch(IWatcher3 watch)
        {
            //_consoleColor = Console.ForegroundColor;

            //watch.OnErrorAsync += OnErrorAsync;
            watch.OnAddedAsync += OnAddedAsync;
            watch.OnRemovedAsync += OnRemovedAsync;
            watch.OnRenamedAsync += OnRenamedAsync;
            watch.OnTouchedAsync += OnTouchedAsync;
        }

        public static async Task WriteException(Exception ex, Context context)
        {
            //if (ConsoleWatch.DoingInitialSync)  //TODO: config
            //    return;


            if (ex is AggregateException aggex)
            {
                await WriteException(aggex.InnerException, context);
                foreach (var aggexInner in aggex.InnerExceptions)
                {
                    await WriteException(aggexInner, context);
                }
                return;
            }

            //Console.WriteLine(ex.Message);
            StringBuilder message = new StringBuilder(ex.Message);
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
                //Console.WriteLine(ex.Message);
                message.Append(Environment.NewLine + ex.Message);
            }

            await AddMessage(ConsoleColor.Red, message.ToString(), context);
        }

        public static string GetNonFullName(string fullName)
        {
            if (fullName.ToUpperInvariant().StartsWith(Global.AsyncPath.ToUpperInvariant()))
            {
                return fullName.Substring(Global.AsyncPath.Length);
            }
            else if (fullName.ToUpperInvariant().StartsWith(Global.SyncPath.ToUpperInvariant()))
            {
                return fullName.Substring(Global.SyncPath.Length);
            }
            else
            {
                throw new ArgumentException("fullName");
            }
        }

        public static string GetOtherFullName(string fullName)
        {
            var nonFullName = GetNonFullName(fullName);

            if (fullName.ToUpperInvariant().StartsWith(Global.AsyncPath.ToUpperInvariant()))
            {
                return Path.Combine(Global.SyncPath, nonFullName);
            }
            else if (fullName.ToUpperInvariant().StartsWith(Global.SyncPath.ToUpperInvariant()))
            {
                return Path.Combine(Global.AsyncPath, nonFullName);
            }
            else
            {
                throw new ArgumentException("fullName");
            }
        }

        public static async Task DeleteFile(string fullName, Context context)
        {
            try
            {
                while (true)
                {
                    context.Token.ThrowIfCancellationRequested();

                    try
                    {
                        if (File.Exists(fullName + "~"))
                        {
#pragma warning disable SEC0116 //Warning	SEC0116	Unvalidated file paths are passed to a file delete API, which can allow unauthorized file system operations (e.g. read, write, delete) to be performed on unintended server files.
                            File.Delete(fullName + "~");
#pragma warning restore SEC0116
                        }

                        if (File.Exists(fullName))
                        {
                            File.Move(fullName, fullName + "~");
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

        public static bool NeedsUpdate(string fullName)
        {
            if (DoingInitialSync)
            {
                return true;
            }

            var converterSaveDate = GetBidirectionalConverterSaveDate(fullName);
            var fileTime = GetFileTime(fullName);

            if (
                !Global.Bidirectional   //no need to debounce BIDIRECTIONAL file save events when bidirectional save is disabled
                || fileTime > converterSaveDate.AddSeconds(3)     //NB! ignore if the file changed during 3 seconds after converter save   //TODO!! config
            )
            {
                var otherFullName = GetOtherFullName(fullName);
                if (fileTime > GetFileTime(otherFullName))     //NB!
                {
                    return true;
                }
            }

            return false;
        }

        public static async Task FileUpdated(string fullName, Context context)
        {
            if (
                IsWatchedFile(fullName)
                && NeedsUpdate(fullName)     //NB!
            )
            {
                var otherFullName = GetOtherFullName(fullName);
                using (await Global.FileOperationLocks.LockAsync(fullName, otherFullName, context.Token))
                {
                    var fullNameInvariant = fullName.ToUpperInvariant();

                    if (
                        Global.WatchedCodeExtension.Any(x => fullNameInvariant.EndsWith("." + x.ToUpperInvariant()))
                        || Global.WatchedCodeExtension.Contains("*")
                    )
                    {
                        if (fullName.ToUpperInvariant().StartsWith(Global.AsyncPath.ToUpperInvariant()))
                        {
                            await AsyncToSyncConverter.AsyncFileUpdated(fullName, context);
                        }
                        else if (fullName.ToUpperInvariant().StartsWith(Global.SyncPath.ToUpperInvariant()))     //NB!
                        {
                            await SyncToAsyncConverter.SyncFileUpdated(fullName, context);
                        }
                        else
                        {
                            throw new ArgumentException("fullName");
                        }
                    }
                    else    //Assume ResX file
                    {
                        //@"\\?\" prefix is needed for reading from long paths: https://stackoverflow.com/questions/44888844/directorynotfoundexception-when-using-long-paths-in-net-4-7
                        var fileData = await FileExtensions.ReadAllBytesAsync(@"\\?\" + fullName, context.Token);
                        var originalData = fileData;

                        //save without transformations
                        await ConsoleWatch.SaveFileModifications(fullName, fileData, originalData, context);
                    }
                }
            }
        }   //public static async Task FileUpdated(string fullName, Context context)

        private static async Task FileDeleted(string fullName, Context context)
        {
            if (IsWatchedFile(fullName))
            {
                if (!File.Exists(fullName))  //NB! verify that the file is still deleted
                {
                    var otherFullName = GetOtherFullName(fullName);

                    await DeleteFile(otherFullName, context);
                }
                else    //NB! file appears to be recreated
                {
                    await FileUpdated(fullName, context);
                }
            }
        }

        private static DateTime GetFileTime(string fullName)
        {
            try
            {
                if (File.Exists(fullName))
                    return File.GetLastWriteTimeUtc(fullName);
                else
                    return DateTime.MinValue;
            }
            catch (FileNotFoundException)    //the file might have been deleted in the meanwhile
            {
                return DateTime.MinValue;
            }
        }

        private static bool IsWatchedFile(string fullName)
        {
            var fullNameInvariant = fullName.ToUpperInvariant();

            if (
                (
                    Global.WatchedCodeExtension.Any(x => fullNameInvariant.EndsWith("." + x.ToUpperInvariant()))
                    || Global.WatchedResXExtension.Any(x => fullNameInvariant.EndsWith("." + x.ToUpperInvariant()))
                    || Global.WatchedCodeExtension.Contains("*")
                    || Global.WatchedResXExtension.Contains("*")
                )
                && 
                Global.ExcludedExtensions.All(x =>

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
                var nonFullNameInvariant = GetNonFullName(fullNameInvariant);

                if (
                    Global.IgnorePathsStartingWith.Any(x => nonFullNameInvariant.StartsWith(x))
                    || Global.IgnorePathsContaining.Any(x => nonFullNameInvariant.Contains(x))
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
            var context = new Context(fse, token);

            try
            {
                if (fse.IsFile)
                {
                    if (IsWatchedFile(fse.PreviousFileSystemInfo.FullName)
                        || IsWatchedFile(fse.FileSystemInfo.FullName))
                    {
                        await AddMessage(ConsoleColor.Cyan, $"[{(fse.IsFile ? "F" : "D")}][R]:{fse.PreviousFileSystemInfo.FullName} > {fse.FileSystemInfo.FullName}", context);

                        //NB! if file is renamed to cs~ or resx~ then that means there will be yet another write to same file, so lets skip this event here
                        if (!fse.FileSystemInfo.FullName.EndsWith("~"))
                        {
                            //using (await Global.FileOperationLocks.LockAsync(rfse.FileSystemInfo.FullName, rfse.PreviousFileSystemInfo.FullName, context.Token))  //comment-out: prevent deadlock
                            {
                                await FileUpdated(fse.FileSystemInfo.FullName, context);
                                await FileDeleted(fse.PreviousFileSystemInfo.FullName, context);
                            }
                        }
                    }
                }
                else
                {
                    await AddMessage(ConsoleColor.Cyan, $"[{(fse.IsFile ? "F" : "D")}][R]:{fse.PreviousFileSystemInfo.FullName} > {fse.FileSystemInfo.FullName}", context);

                    //TODO trigger update / delete event for all files in new folder
                }
            }
            catch (Exception ex)
            {
                await WriteException(ex, context);
            }
        }   //private static async Task OnRenamedAsync(IRenamedFileSystemEvent fse, CancellationToken token)

        private static async Task OnRemovedAsync(IFileSystemEvent fse, CancellationToken token)
        {
            var context = new Context(fse, token);

            try
            {
                if (fse.IsFile)
                {
                    if (IsWatchedFile(fse.FileSystemInfo.FullName))
                    {
                        await AddMessage(ConsoleColor.Yellow, $"[{(fse.IsFile ? "F" : "D")}][-]:{fse.FileSystemInfo.FullName}", context);

                        using (await FileEventLocks.LockAsync(fse.FileSystemInfo.FullName, token))
                        {
                            await FileDeleted(fse.FileSystemInfo.FullName, context);
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

        public static async Task OnAddedAsync(IFileSystemEvent fse, CancellationToken token)
        {
            var context = new Context(fse, token);

            try
            {
                if (fse.IsFile)
                {
                    if (IsWatchedFile(fse.FileSystemInfo.FullName))
                    {
                        //await AddMessage(ConsoleColor.Green, $"[{(fse.IsFile ? "F" : "D")}][+]:{fse.FileSystemInfo.FullName}", context);

                        using (await FileEventLocks.LockAsync(fse.FileSystemInfo.FullName, token))
                        {
                            await FileUpdated(fse.FileSystemInfo.FullName, context);
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
            var context = new Context(fse, token);

            try
            {
                if (
                    fse.IsFile
                    && File.Exists(fse.FileSystemInfo.FullName)     //for some reason fse.IsFile is set even for folders
                )
                {
                    if (IsWatchedFile(fse.FileSystemInfo.FullName))
                    {
                        await AddMessage(ConsoleColor.Gray, $"[{(fse.IsFile ? "F" : "D")}][T]:{fse.FileSystemInfo.FullName}", context);

                        using (await FileEventLocks.LockAsync(fse.FileSystemInfo.FullName, token))
                        {
                            await FileUpdated(fse.FileSystemInfo.FullName, context);
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

        //private async Task OnErrorAsync(IEventError ee, CancellationToken token)
        //{
        //    try
        //    { 
        //        await AddMessage(ConsoleColor.Red, $"[!]:{ee.Message}", context);
        //    }
        //    catch (Exception ex)
        //    {
        //        await WriteException(ex, context);
        //    }
        //}

        public static async Task AddMessage(ConsoleColor color, string message, Context context)
        {
            await Task.Run(() =>
            {
                lock (Lock)
                //using (await AsyncLock.LockAsync())
                {
                    try
                    {
                        Console.ForegroundColor = color;
                        Console.WriteLine($"[{context.Time.ToLocalTime():yyyy.MM.dd HH:mm:ss.ffff}]:{message}");
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
            }, context.Token);
        }

        public static async Task SaveFileModifications(string fullName, string fileData, string originalData, Context context)
        {
            var otherFullName = GetOtherFullName(fullName);


            //NB! detect whether the file actually changed
            var otherFileData = File.Exists(otherFullName)
                //@"\\?\" prefix is needed for reading from long paths: https://stackoverflow.com/questions/44888844/directorynotfoundexception-when-using-long-paths-in-net-4-7
                ? await FileExtensions.ReadAllTextAsync(@"\\?\" + otherFullName, context.Token)     //TODO: optimisation: no need to read the bytes in case the file lenghts are different
                : null;

            if (
                (otherFileData?.Length ?? -1) != fileData.Length 
                || otherFileData != fileData
            )
            {
                await DeleteFile(otherFullName, context);

                var otherDirName = Path.GetDirectoryName(otherFullName);
                if (!Directory.Exists(otherDirName))
                    Directory.CreateDirectory(otherDirName);

                //@"\\?\" prefix is needed for writing to long paths: https://stackoverflow.com/questions/44888844/directorynotfoundexception-when-using-long-paths-in-net-4-7
                await FileExtensions.WriteAllTextAsync(@"\\?\" + otherFullName, fileData, context.Token);

                var now = DateTime.UtcNow;  //NB! compute now after saving the file
                BidirectionalConverterSavedFileDates[otherFullName] = now;


                await AddMessage(ConsoleColor.Magenta, $"Synchronised updates from file {fullName}", context);
            }
            else if (false)
            {
                //touch the file
                var now = DateTime.UtcNow;  //NB! compute common now for ConverterSavedFileDates

                try
                {
                    File.SetLastWriteTimeUtc(otherFullName, now);
                }
                catch (Exception ex)
                {
                    await ConsoleWatch.WriteException(ex, context);
                }

                BidirectionalConverterSavedFileDates[otherFullName] = now;
            }
        }   //public static async Task SaveFileModifications(string fullName, string fileData, string originalData, Context context)

        public static async Task SaveFileModifications(string fullName, byte[] fileData, byte[] originalData, Context context)
        {
            var otherFullName = GetOtherFullName(fullName);


            //NB! detect whether the file actually changed
            var otherFileData = File.Exists(otherFullName)
                //@"\\?\" prefix is needed for reading from long paths: https://stackoverflow.com/questions/44888844/directorynotfoundexception-when-using-long-paths-in-net-4-7
                ? await FileExtensions.ReadAllBytesAsync(@"\\?\" + otherFullName, context.Token)    //TODO: optimisation: no need to read the bytes in case the file lenghts are different
                : null;

            if (
                (otherFileData?.Length ?? -1) != fileData.Length 
                || !FileExtensions.BinaryEqual(otherFileData, fileData)
            )
            {
                await DeleteFile(otherFullName, context);

                var otherDirName = Path.GetDirectoryName(otherFullName);
                if (!Directory.Exists(otherDirName))
                    Directory.CreateDirectory(otherDirName);

                //@"\\?\" prefix is needed for writing to long paths: https://stackoverflow.com/questions/44888844/directorynotfoundexception-when-using-long-paths-in-net-4-7
                await FileExtensions.WriteAllBytesAsync(@"\\?\" + otherFullName, fileData, context.Token);

                var now = DateTime.UtcNow;  //NB! compute now after saving the file
                BidirectionalConverterSavedFileDates[otherFullName] = now;


                await AddMessage(ConsoleColor.Magenta, $"Synchronised updates from file {fullName}", context);
            }
            else if (false)
            {
                //touch the file
                var now = DateTime.UtcNow;  //NB! compute common now for ConverterSavedFileDates

                try
                {
                    File.SetLastWriteTimeUtc(otherFullName, now);
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
}
