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
    }
}
