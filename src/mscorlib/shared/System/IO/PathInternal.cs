// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text;

namespace System.IO
{
    /// <summary>Contains internal path helpers that are shared between many projects.</summary>
    internal static partial class PathInternal
    {
        /// <summary>
        /// Returns true if the path ends in a directory separator.
        /// </summary>
        internal static bool EndsInDirectorySeparator(ReadOnlySpan<char> path)
            => path.Length > 0 && IsDirectorySeparator(path[path.Length - 1]);

        /// <summary>
        /// Returns true if the path starts in a directory separator.
        /// </summary>
        internal static bool StartsWithDirectorySeparator(ReadOnlySpan<char> path) => path.Length > 0 && IsDirectorySeparator(path[0]);

        internal static string EnsureTrailingSeparator(string path)
            => EndsInDirectorySeparator(path) ? path : path + DirectorySeparatorCharAsString;

        internal static string TrimEndingDirectorySeparator(string path) =>
            EndsInDirectorySeparator(path) && !IsRoot(path) ?
                path.Substring(0, path.Length - 1) :
                path;

        internal static ReadOnlySpan<char> TrimEndingDirectorySeparator(ReadOnlySpan<char> path) =>
            EndsInDirectorySeparator(path) && !IsRoot(path) ?
                path.Slice(0, path.Length - 1) :
                path;

        internal static bool IsRoot(ReadOnlySpan<char> path)
            => path.Length == GetRootLength(path);

        /// <summary>
        /// Get the common path length from the start of the string.
        /// </summary>
        internal static int GetCommonPathLength(string first, string second, bool ignoreCase)
        {
            int commonChars = EqualStartingCharacterCount(first, second, ignoreCase: ignoreCase);

            // If nothing matches
            if (commonChars == 0)
                return commonChars;

            // Or we're a full string and equal length or match to a separator
            if (commonChars == first.Length
                && (commonChars == second.Length || IsDirectorySeparator(second[commonChars])))
                return commonChars;

            if (commonChars == second.Length && IsDirectorySeparator(first[commonChars]))
                return commonChars;

            // It's possible we matched somewhere in the middle of a segment e.g. C:\Foodie and C:\Foobar.
            while (commonChars > 0 && !IsDirectorySeparator(first[commonChars - 1]))
                commonChars--;

            return commonChars;
        }

        /// <summary>
        /// Gets the count of common characters from the left optionally ignoring case
        /// </summary>
        internal static unsafe int EqualStartingCharacterCount(string first, string second, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return 0;

            int commonChars = 0;

            fixed (char* f = first)
            fixed (char* s = second)
            {
                char* l = f;
                char* r = s;
                char* leftEnd = l + first.Length;
                char* rightEnd = r + second.Length;

                while (l != leftEnd && r != rightEnd
                    && (*l == *r || (ignoreCase && char.ToUpperInvariant((*l)) == char.ToUpperInvariant((*r)))))
                {
                    commonChars++;
                    l++;
                    r++;
                }
            }

            return commonChars;
        }

        /// <summary>
        /// Returns true if the two paths have the same root
        /// </summary>
        internal static bool AreRootsEqual(string first, string second, StringComparison comparisonType)
        {
            int firstRootLength = GetRootLength(first);
            int secondRootLength = GetRootLength(second);

            return firstRootLength == secondRootLength
                && string.Compare(
                    strA: first,
                    indexA: 0,
                    strB: second,
                    indexB: 0,
                    length: firstRootLength,
                    comparisonType: comparisonType) == 0;
        }

        /// <summary>
        /// Try to remove relative segments from the given path (without combining with a root).
        /// </summary>
        /// <param name="skip">Skip the specified number of characters before evaluating.</param>
        internal static string RemoveRelativeSegments(string path, int skip = 0)
        {
            Debug.Assert(skip >= 0);
            bool flippedSeparator = false;

            // Remove "//", "/./", and "/../" from the path by copying each character to the output, 
            // except the ones we're removing, such that the builder contains the normalized path 
            // at the end.
            StringBuilder sb = StringBuilderCache.Acquire(path.Length);
            if (skip > 0)
            {
                sb.Append(path, 0, skip);
            }

            for (int i = skip; i < path.Length; i++)
            {
                char c = path[i];

                if (PathInternal.IsDirectorySeparator(c) && i + 1 < path.Length)
                {
                    // Skip this character if it's a directory separator and if the next character is, too,
                    // e.g. "parent//child" => "parent/child"
                    if (PathInternal.IsDirectorySeparator(path[i + 1]))
                    {
                        continue;
                    }

                    // Skip this character and the next if it's referring to the current directory,
                    // e.g. "parent/./child" => "parent/child"
                    if ((i + 2 == path.Length || PathInternal.IsDirectorySeparator(path[i + 2])) &&
                        path[i + 1] == '.')
                    {
                        i++;
                        continue;
                    }

                    // Skip this character and the next two if it's referring to the parent directory,
                    // e.g. "parent/child/../grandchild" => "parent/grandchild"
                    if (i + 2 < path.Length &&
                        (i + 3 == path.Length || PathInternal.IsDirectorySeparator(path[i + 3])) &&
                        path[i + 1] == '.' && path[i + 2] == '.')
                    {
                        // Unwind back to the last slash (and if there isn't one, clear out everything).
                        int s;
                        for (s = sb.Length - 1; s >= skip; s--)
                        {
                            if (PathInternal.IsDirectorySeparator(sb[s]))
                            {
                                sb.Length = (i + 3 >= path.Length && s == skip) ? s + 1 : s; // to avoid removing the complete "\tmp\" segment in cases like \\?\C:\tmp\..\, C:\tmp\..
                                break;
                            }
                        }
                        if (s < skip)
                        {
                            sb.Length = skip;
                        }

                        i += 2;
                        continue;
                    }
                }

                // Normalize the directory separator if needed
                if (c != PathInternal.DirectorySeparatorChar && c == PathInternal.AltDirectorySeparatorChar)
                {
                    c = PathInternal.DirectorySeparatorChar;
                    flippedSeparator = true;
                }

                sb.Append(c);
            }

            if (flippedSeparator || sb.Length != path.Length)
            {
                return StringBuilderCache.GetStringAndRelease(sb);
            }
            else
            {
                // We haven't changed the source path, return the original
                StringBuilderCache.Release(sb);
                return path;
            }
        }
    }
}
