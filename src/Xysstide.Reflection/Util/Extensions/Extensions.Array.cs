using System;

namespace Xysstide.Reflection.Util.Extensions;

partial class Extensions
{
    // Included internally until more of the Xysstide.Common project is made open-source.
    internal static TSource[] FastWhere<TSource>(this TSource[] array, Func<TSource, bool> predicate) {
        if (array is null) throw new ArgumentNullException(nameof(array));

        if (array.Length == 0) return array;

        int index  = 0;
        var result = new TSource[array.Length];

        // ReSharper disable once ForCanBeConvertedToForeach
        for (int i = 0; i < array.Length; i++) {
            var element = array[i];

            if (!predicate(element)) continue;

            result[index] = element;
            index++;
        }

        return index == 0 ? Array.Empty<TSource>() : result[..index];
    }
}
