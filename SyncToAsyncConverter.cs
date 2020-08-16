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
    using KVP = Tuple<Regex, string>;

    static class SyncToAsyncConverter
    {
        public static readonly List<KVP> Replacements = new List<KVP>();


        //TODO config file

        //private static readonly Regex AsyncLockReplaceRegex = new Regex(@" /*--AsyncLock--*/\s*object", RegexOptions.Singleline | RegexOptions.Compiled);   //NB! VS may add newline after the comment
        //private static readonly string AsyncLockReplaceRegexReplacement = @"$1AsyncLock";


        private static readonly Regex TaskDelayReplaceRegex = new Regex(@"/[*]--Task[.]Delay--[*]/\s*((System[.])?Threading[.])?Thread[.]Sleep", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string TaskDelayReplaceRegexReplacement = @"Task.Delay";


        private static readonly Regex TaskReplaceRegex = new Regex(@"(\s+)/[*]--(async\s+)?Task<--[*]/\s*([^/*(-]+)/[*]-->--[*]/(\s+)", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string TaskReplaceRegexReplacement = @"$1$2Task<$3>$4";


        private static readonly Regex AsyncLockReplaceRegex = new Regex(@"/[*]--using--[*]/\s*lock(\s*)[(]/[*]--await--[*]/\s+([^/*()-]+)/[*]--[.]Lock\sA\ss\sy\sn\sc[(][)]--[*]/\s*[)]", RegexOptions.Singleline | RegexOptions.Compiled);
        private const string AsyncLockReplaceRegexReplacement = @"using$1(await $2.LockAsync())";

        static SyncToAsyncConverter()
        {
            var asyncToSyncReplacements = AsyncToSyncConverter.Replacements.GetReverse();   //NB! use reverse order

            //Convert each replacement to regex since we need to augment them a bit 
            //- VS may add newline after the comment and that needs to be handled
            //For example:
            // " /*--AsyncLock--*/\s*object" --> " AsyncLock"

            var escapedCommentEnd = Regex.Escape(@"*/");

            foreach (var replacement in asyncToSyncReplacements)
            {
                var regexString = Regex.Escape(replacement.Item2);
                regexString = regexString.Replace(escapedCommentEnd, escapedCommentEnd + @"\s*");
                var regex = new Regex(regexString, RegexOptions.Singleline | RegexOptions.Compiled);

                Replacements.Add(new KVP(regex, replacement.Item1));
            }
        }

        public static async Task SyncFileUpdated(string fullName, Context context)
        {
            using (await Global.FileOperationAsyncLock.LockAsync())
            {
                var fileData = await FileExtensions.ReadAllTextAsync(fullName, context.Token);
                var originalData = fileData;



                fileData = TaskDelayReplaceRegex.Replace(fileData, TaskDelayReplaceRegexReplacement);
                fileData = TaskReplaceRegex.Replace(fileData, TaskReplaceRegexReplacement);
                fileData = AsyncLockReplaceRegex.Replace(fileData, AsyncLockReplaceRegexReplacement);


                foreach (var replacement in Replacements)
                {
                    fileData = replacement.Item1.Replace(fileData, replacement.Item2);
                }



                await ConsoleWatch.SaveFileModifications(fullName, fileData, originalData, context);

            }   //using (await Global.FileOperationAsyncLock.LockAsync())
        }
    }
}
