//
// Copyright (c) Roland Pihlakas 2019 - 2020
// roland@simplify.ee
//
// Roland Pihlakas licenses this file to you under the GNU Lesser General Public License, ver 2.1.
// See the LICENSE file for more information.
//

#define ASYNC
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AsyncToSyncCodeRoundtripSynchroniserMonitor
{
    using KVP = Tuple<string, string>;

    static class AsyncToSyncConverter
    {
        //TODO config file
        public static readonly List<KVP> Replacements = new List<KVP>()
        {
            //NB! VS may put the /*--await--*/ on a separate line therefore need handling for various space types after /*--await--*/
            new KVP("await ", "/*--await--*/ "),
            new KVP("await\t", "/*--await--*/\t"),
            new KVP("await\r", "/*--await--*/\r"),
            new KVP("await\n", "/*--await--*/\n"),

            new KVP(@".SendMailAsync(", @".Send/*--MailAsync--*/("),
            new KVP(@"Async(", @"/*--Async--*/("),

            new KVP(@" async Task ", @" /*--async Task--*/void "),
            //new KVP(@" async void ", @" /*--async void--*/void "),
            new KVP(@" async ", @" /*--async--*/ "),
            new KVP(@" Task ", @" /*--Task--*/void "),  //method return type
            new KVP(@"Task ", @"/*--Task--*/Action "),  //method argument type

            new KVP(@").Wait();", @")/*--.Wait()--*/;"),
            //new KVP("Task.Delay", "/*--Task.Delay--*/System.Threading.Thread.Sleep"),     //this needs special regex in sync to async direction
            new KVP(@"Task.FromResult", @"/*--Task.FromResult--*/"),
            new KVP(@" AsyncLock", @" /*--AsyncLock--*/object"),    

            new KVP(@"#define ASYNC", @"#define NOASYNC"),
        };


        //private static readonly Regex AsyncLockReplaceRegex = new Regex(@"(\s+)AsyncLock", RegexOptions.Singleline | RegexOptions.Compiled);
        //private static readonly string AsyncLockReplaceRegexReplacement = @"$1/*--AsyncLock--*/object";


        private static readonly Regex TaskDelayReplaceRegex = new Regex(@"Task[.]Delay", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string TaskDelayReplaceRegexReplacement = @"/*--Task.Delay--*/System.Threading.Thread.Sleep";


        private static readonly Regex TaskReplaceRegex = new Regex(@"(\s+)(async\s+)?Task<([^ (]+)>(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string TaskReplaceRegexReplacement = @"$1/*--$2Task<--*/$3/*-->--*/$4";


        private static readonly Regex AsyncLockReplaceRegex = new Regex(@"using([^(]*)[(]await(\s+[^(]+)[.]LockAsync[(][)][)]", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string AsyncLockReplaceRegexReplacement = @"/*--using--*/lock$1(/*--await--*/ $2/*--.Lock A s y n c()--*/)";


        public static async Task AsyncFileUpdated(string fullName, Context context)
        {
            //using (await Global.FileOperationAsyncLock.LockAsync())
            {
                //@"\\?\" prefix is needed for reading from long paths: https://stackoverflow.com/questions/44888844/directorynotfoundexception-when-using-long-paths-in-net-4-7
                var fileData = await FileExtensions.ReadAllTextAsync(@"\\?\" + fullName, context.Token);
                var originalData = fileData;



                fileData = AsyncLockReplaceRegex.Replace(fileData, AsyncLockReplaceRegexReplacement);
                fileData = TaskReplaceRegex.Replace(fileData, TaskReplaceRegexReplacement);
                fileData = TaskDelayReplaceRegex.Replace(fileData, TaskDelayReplaceRegexReplacement);


                foreach (var replacement in Replacements)
                {
                    fileData = fileData.Replace(replacement.Item1, replacement.Item2);
                }


                await ConsoleWatch.SaveFileModifications(fullName, fileData, originalData, context);

            }   //using (await Global.FileOperationAsyncLock.LockAsync())
        }
    }
}
