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
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AsyncToSyncCodeRoundtripSynchroniserMonitor
{
    using KVP = Tuple<Regex, string>;

    static class SyncToAsyncConverter
    {
        public static readonly List<KVP> CS_Replacements = new List<KVP>();


        //TODO config file

        //private static readonly Regex AsyncLockReplaceRegex = new Regex(@" /*--AsyncLock--*/\s*object", RegexOptions.Singleline | RegexOptions.Compiled);   //NB! VS may add newline after the comment
        //private static readonly string AsyncLockReplaceRegexReplacement = @"$1AsyncLock";


        private static readonly Regex CS_TaskDelayReplaceRegex = new Regex(@"/[*]--Task[.]Delay--[*]/\s*((System[.])?Threading[.])?Thread[.]Sleep", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string CS_TaskDelayReplaceRegexReplacement = @"Task.Delay";


        private static readonly Regex CS_TaskReplaceRegex = new Regex(@"(\s+)/[*]--(async\s+)?Task<--[*]/\s*([^/*(-]+)/[*]-->--[*]/(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string CS_TaskReplaceRegexReplacement = @"$1$2Task<$3>$4";


        private static readonly Regex CS_AsyncLockReplaceRegex = new Regex(@"/[*]--using--[*]/\s*lock(\s*)[(]/[*]--await--[*]/\s+([^/*()-]+)/[*]--[.]Lock\sA\ss\sy\sn\sc[(][)]--[*]/\s*[)]", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string CS_AsyncLockReplaceRegexReplacement = @"using$1(await $2.LockAsync())";


        private static readonly Regex CS_FuncTaskReplaceRegex = new Regex(@"([\s,(]+)Func</[*]--Task<--[*]/([^=)]+)/[*]-->--[*]/>", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string CS_FuncTaskReplaceRegexReplacement = @"$1Func<Task<$2>>";




        public static readonly List<KVP> PY_Replacements = new List<KVP>();


        private static readonly Regex PY_AwaitReplaceRegex = new Regex(@"(\n\r|\r\n|\n)#--(\s*)await(\s+)--(\n\r|\r\n|\n)(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string PY_AwaitReplaceRegexReplacement = @"$1$5await$3";


        private static readonly Regex PY_Await2ReplaceRegex = new Regex(@"=#--(\s*)await(\s+)--(\n\r|\r\n|\n)(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string PY_Await2ReplaceRegexReplacement = @"=$1await$2";


        private static readonly Regex PY_AsyncReplaceRegex = new Regex(@"(\n\r|\r\n|\n)#--(\s*)async(\s+)--(\n\r|\r\n|\n)(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string PY_AsyncRegexReplacement = @"$1$5async$3";




        static SyncToAsyncConverter()
        {
            var cs_asyncToSyncReplacements = AsyncToSyncConverter.CS_Replacements.GetReverse();   //NB! use reverse order

            //Convert each replacement to regex since we need to augment them a bit 
            //- VS may add newline after the comment and that needs to be handled
            //For example:
            // " /*--AsyncLock--*/\s*object" --> " AsyncLock"

            var cs_escapedCommentEnd = Regex.Escape(@"*/");

            foreach (var replacement in cs_asyncToSyncReplacements)
            {
                var regexString = Regex.Escape(replacement.Item2);
                regexString = regexString.Replace(cs_escapedCommentEnd, cs_escapedCommentEnd + @"\s*");
                var regex = new Regex(regexString, RegexOptions.Singleline | RegexOptions.Compiled);

                CS_Replacements.Add(new KVP(regex, replacement.Item1));
            }



            var py_asyncToSyncReplacements = AsyncToSyncConverter.PY_Replacements.GetReverse();   //NB! use reverse order

            foreach (var replacement in py_asyncToSyncReplacements)
            {
                var regexString = Regex.Escape(replacement.Item2);
                var regex = new Regex(regexString, RegexOptions.Singleline | RegexOptions.Compiled);

                PY_Replacements.Add(new KVP(regex, replacement.Item1));
            }
        }   //static SyncToAsyncConverter()

        public static async Task SyncFileUpdated(Context context)
        {
            //using (await Global.FileOperationAsyncLock.LockAsync())
            {
                var fileData = await FileExtensions.ReadAllTextAsync(Extensions.GetLongPath(context.Event.FullName), context.Token);
                var originalData = fileData;
                

                if (context.Event.FullName.EndsWith(".cs"))
                {
                    fileData = CS_TaskDelayReplaceRegex.Replace(fileData, CS_TaskDelayReplaceRegexReplacement);
                    fileData = CS_TaskReplaceRegex.Replace(fileData, CS_TaskReplaceRegexReplacement);
                    fileData = CS_AsyncLockReplaceRegex.Replace(fileData, CS_AsyncLockReplaceRegexReplacement);
                    fileData = CS_FuncTaskReplaceRegex.Replace(fileData, CS_FuncTaskReplaceRegexReplacement);

                    foreach (var replacement in CS_Replacements)
                    {
                        fileData = replacement.Item1.Replace(fileData, replacement.Item2);
                    }
                }
                else if (context.Event.FullName.EndsWith(".py"))
                {
                    fileData = PY_AsyncReplaceRegex.Replace(fileData, PY_AsyncRegexReplacement);
                    fileData = PY_AwaitReplaceRegex.Replace(fileData, PY_AwaitReplaceRegexReplacement);

                    foreach (var replacement in PY_Replacements)
                    {
                        fileData = replacement.Item1.Replace(fileData, replacement.Item2);
                    }
                }
                else
                {
                    throw new NotImplementedException("Unknown file extension");
                }


                await ConsoleWatch.SaveFileModifications(fileData, originalData, context);

            }   //using (await Global.FileOperationAsyncLock.LockAsync())
        }   //public static async Task SyncFileUpdated(string fullName, Context context)
    }
}
