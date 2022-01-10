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

namespace AsyncToSyncCodeRoundtripSynchroniserMonitor
{
    public static class Extensions
    {
        public static List<T> GetReverse<T>(this List<T> list)
        {
            var newList = new List<T>(list);
            newList.Reverse();
            return newList;
        }

        public static List<Tuple<TValue, TKey>> Inverse<TKey, TValue>(this List<Tuple<TKey, TValue>> source)
        {
            var result = new List<Tuple<TValue, TKey>>();

            foreach (var entry in source)
            {
                result.Add(new Tuple<TValue, TKey>(entry.Item2, entry.Item1));
            }

            return result;
        }

        public static Dictionary<TValue, TKey> Inverse<TKey, TValue>(this IDictionary<TKey, TValue> source)
        {
            var result = new Dictionary<TValue, TKey>();

            foreach (var entry in source)
            {
                if (!result.ContainsKey(entry.Value))
                    result.Add(entry.Value, entry.Key);
            }

            return result;
        }

        public static string GetLongPath(string path)
        {
            //@"\\?\" prefix is needed for reading from long paths: https://stackoverflow.com/questions/44888844/directorynotfoundexception-when-using-long-paths-in-net-4-7 and https://superuser.com/questions/1617012/support-of-the-unc-server-share-syntax-in-windows

            if (path.Substring(0, 2) == @"\\")   //network path
            {
                //return @"\\?\UNC" + path.Substring(1);
                return path;
            }
            else
            {
                return @"\\?\" + path;
            }
        }
    }
}
