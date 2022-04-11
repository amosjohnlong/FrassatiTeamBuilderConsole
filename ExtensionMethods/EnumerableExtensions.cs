using System;
using System.Collections.Generic;
using System.Linq;

namespace FrassatiTeamBuilderConsole.ExtensionMethods
{
    public static class EnumerableExtensions
    {
        public static T[] ForEach<T>(this T[] array, Action<T> action)
        {
            Array.ForEach(array, action);
            return array;
        }

        public static double AverageOrZero<T>(this IEnumerable<T> source, Func<T, double> func)
        {
            // TODO: decide what to do if source equals null: exception or return default?
            if (source.Any())
                return source.Average(func);
            else
                return 0;
        }
    }
}
