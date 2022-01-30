//
// Copyright (c) Roland Pihlakas 2019 - 2022
// roland@simplify.ee
//
// Roland Pihlakas licenses this file to you under the GNU Lesser General Public License, ver 2.1.
// See the LICENSE file for more information.
//

#define ASYNC
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AsyncToSyncCodeRoundtripSynchroniserMonitor
{
    using KVP = Tuple<string, string>;

    static class AsyncToSyncConverter
    {
        //TODO config file
        public static readonly List<KVP> CS_Replacements = new List<KVP>()
        {
            //NB! VS may put the /*--await--*/ on a separate line therefore need handling for various space types after /*--await--*/
            //TODO!: ensure that await starts at word boundary
            new KVP("await ", "/*--await--*/ "),
            new KVP("await\t", "/*--await--*/\t"),
            new KVP("await\r", "/*--await--*/\r"),
            new KVP("await\n", "/*--await--*/\n"),

            new KVP(@".SendMailAsync(", @".Send/*--MailAsync--*/("),
            new KVP(@"Async(", @"/*--Async--*/("),

            new KVP(@" async Task ", @" /*--async Task--*/void "),
            //new KVP(@" async void ", @" /*--async void--*/void "),
            new KVP(@" async IAsyncEnumerable", @" /*--async IAsyncEnumerable--*/IEnumerable"),
            new KVP(@" async ", @" /*--async--*/ "),

            new KVP("(async ", "(/*--async--*/ "),
            new KVP("(async\t", "(/*--async--*/\t"),
            new KVP("(async\r", "(/*--async--*/\r"),
            new KVP("(async\n", "(/*--async--*/\n"),

            new KVP(@", Task ", @", /*--Task--*/Action "),  //method argument type
            new KVP(@"(Task ", @"(/*--Task--*/Action "),  //method argument type
            new KVP(@", Task<T> ", @", /*--Task<T>--*/Func<T> "),  //method argument type
            new KVP(@"(Task<T> ", @"(/*--Task<T>--*/Func<T> "),  //method argument type
            new KVP(@" Task ", @" /*--Task--*/void "),  //method return type

            new KVP(@").Wait();", @")/*--.Wait()--*/;"),
            //new KVP("Task.Delay", "/*--Task.Delay--*/System.Threading.Thread.Sleep"),     //this needs special regex in sync to async direction
            new KVP(@"Task.FromResult", @"/*--Task.FromResult--*/"),
            new KVP(@"Task.WhenAll", @"/*--Task.WhenAll--*/"),

            //TODO: use Regex instead since the match beginning may also vary from space
            new KVP(" AsyncLock ", " /*--AsyncLock--*/object "),      //TODO!!! add handling for \t \r \n 
            new KVP(" AsyncLock(", " /*--AsyncLock--*/object("),
            new KVP(" AsyncLock>", " /*--AsyncLock--*/object>"),
            new KVP(" AsyncLock,", " /*--AsyncLock--*/object,"),
            new KVP(" AsyncLock\t", " /*--AsyncLock--*/object\t"),
            new KVP(" AsyncLock\r", " /*--AsyncLock--*/object\r"),
            new KVP(" AsyncLock\n", " /*--AsyncLock--*/object\n"),

            new KVP(@"#define ASYNC", @"#define NOASYNC"),
        };


        //private static readonly Regex AsyncLockReplaceRegex = new Regex(@"(\s+)AsyncLock", RegexOptions.Singleline | RegexOptions.Compiled);
        //private static readonly string AsyncLockReplaceRegexReplacement = @"$1/*--AsyncLock--*/object";


        private static readonly Regex CS_TaskDelayReplaceRegex = new Regex(@"Task[.]Delay", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string CS_TaskDelayReplaceRegexReplacement = @"/*--Task.Delay--*/System.Threading.Thread.Sleep";


        private static readonly Regex CS_TaskReplaceRegex = new Regex(@"(\s+)(async\s+)?Task<([^(]+)>(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string CS_TaskReplaceRegexReplacement = @"$1/*--$2Task<--*/$3/*-->--*/$4";


        private static readonly Regex CS_AsyncLockReplaceRegex = new Regex(@"using([^(]*)[(]await(\s+[^(]+)[.]LockAsync[(][)][)]", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string CS_AsyncLockReplaceRegexReplacement = @"/*--using--*/lock$1(/*--await--*/ $2/*--.Lock A s y n c()--*/)";


        private static readonly Regex CS_FuncTaskReplaceRegex = new Regex(@"([\s,(]+)Func<Task<([^=)]+)>>", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string CS_FuncTaskReplaceRegexReplacement = @"$1Func</*--Task<--*/$2/*-->--*/>";




        public static readonly List<KVP> PY_Replacements = new List<KVP>()
        {
            //TODO!: ensure that matches start at word boundary    

            new KVP(@"ASYNC = True", @"ASYNC = False"),
            new KVP(@"NOASYNC = False", @"NOASYNC = True"),

            new KVP(" aiofiles.open", " open"),
            new KVP("\taiofiles.open", "\topen"),
            new KVP("\raiofiles.open", "\ropen"),
            new KVP("\naiofiles.open", "\nopen"),

            new KVP(" open", " io.open"),
            new KVP("\topen", "\tio.open"),
            new KVP("\ropen", "\rio.open"),
            new KVP("\nopen", "\nio.open"),
        };


        private static readonly Regex PY_AwaitReplaceRegex = new Regex(@"(\n\r|\r\n|\n)(\s*)await(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string PY_AwaitReplaceRegexReplacement = @"$1#--$2await$3--$1";


        private static readonly Regex PY_Await2ReplaceRegex = new Regex(@"=(\s*)await(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string PY_Await2ReplaceRegexReplacement = @"=#--$1await$2--$1";


        private static readonly Regex PY_AsyncReplaceRegex = new Regex(@"(\n\r|\r\n|\n)(\s*)async(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string PY_AsyncRegexReplacement = @"$1#--$2async$3--$2";




        public static async Task AsyncFileUpdated(Context context)
        {
            //using (await Global.FileOperationAsyncLock.LockAsync())
            {
                string fileData;
                try
                {
                    long maxFileSize = Global.MaxFileSizeMB * (1024 * 1024);
                    fileData = await FileExtensions.ReadAllTextAsync(Extensions.GetLongPath(context.Event.FullName), context.Token, maxFileSize: maxFileSize, retryCount: Global.RetryCountOnSrcFileOpenError);

                    if (fileData == null)
                    {
                        await ConsoleWatch.AddMessage(ConsoleColor.Red, $"Error synchronising updates from file {context.Event.FullName}", context);
                    }
                }
                catch (FileNotFoundException)   //file was removed by the time queue processing got to it
                {
                    return;     
                }


                var originalData = fileData;


                if (context.Event.FullName.EndsWith(".cs"))
                {
                    foreach (var replacement in CS_Replacements)
                    {
                        fileData = fileData.Replace(replacement.Item1, replacement.Item2);
                    }

                    fileData = CS_FuncTaskReplaceRegex.Replace(fileData, CS_FuncTaskReplaceRegexReplacement);
                    fileData = CS_AsyncLockReplaceRegex.Replace(fileData, CS_AsyncLockReplaceRegexReplacement);
                    fileData = CS_TaskReplaceRegex.Replace(fileData, CS_TaskReplaceRegexReplacement);
                    fileData = CS_TaskDelayReplaceRegex.Replace(fileData, CS_TaskDelayReplaceRegexReplacement);
                }
                else if (context.Event.FullName.EndsWith(".py"))
                {
                    foreach (var replacement in PY_Replacements)
                    {
                        fileData = fileData.Replace(replacement.Item1, replacement.Item2);
                    }

                    fileData = PY_AwaitReplaceRegex.Replace(fileData, PY_AwaitReplaceRegexReplacement);
                    fileData = PY_AsyncReplaceRegex.Replace(fileData, PY_AsyncRegexReplacement);
                }
                else
                {
                    throw new NotImplementedException("Unknown file extension");
                }


                await ConsoleWatch.SaveFileModifications(fileData, originalData, context);

            }   //using (await Global.FileOperationAsyncLock.LockAsync())
        }   //public static async Task AsyncFileUpdated(string fullName, Context context)
    }
}
