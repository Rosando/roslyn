﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.PatternMatching
{
    /// <summary>
    /// The pattern matcher is thread-safe.  However, it maintains an internal cache of
    /// information as it is used.  Therefore, you should not keep it around forever and should get
    /// and release the matcher appropriately once you no longer need it.
    /// Also, while the pattern matcher is culture aware, it uses the culture specified in the
    /// constructor.
    /// </summary>
    internal abstract partial class PatternMatcher : IDisposable
    {
        private static readonly char[] s_dotCharacterArray = { '.' };

        public const int NoBonus = 0;
        public const int CamelCaseContiguousBonus = 1;
        public const int CamelCaseMatchesFromStartBonus = 2;
        public const int CamelCaseMaxWeight = CamelCaseContiguousBonus + CamelCaseMatchesFromStartBonus;

        private readonly object _gate = new object();

        private readonly bool _includeMatchedSpans;
        private readonly bool _allowFuzzyMatching;

        private readonly Dictionary<string, StringBreaks> _stringToWordSpans = new Dictionary<string, StringBreaks>();
        private static readonly Func<string, StringBreaks> _breakIntoWordSpans = StringBreaker.BreakIntoWordParts;

        // PERF: Cache the culture's compareInfo to avoid the overhead of asking for them repeatedly in inner loops
        private readonly CompareInfo _compareInfo;

        private bool _invalidPattern;
        /// <summary>
        /// Construct a new PatternMatcher using the specified culture.
        /// </summary>
        /// <param name="culture">The culture to use for string searching and comparison.</param>
        /// <param name="includeMatchedSpans">Whether or not the matching parts of the candidate should be supplied in results.</param>
        /// <param name="allowFuzzyMatching">Whether or not close matches should count as matches.</param>
        protected PatternMatcher(
            bool includeMatchedSpans,
            CultureInfo culture,
            bool allowFuzzyMatching = false)
        {
            culture = culture ?? CultureInfo.CurrentCulture;
            _compareInfo = culture.CompareInfo;
            _includeMatchedSpans = includeMatchedSpans;
            _allowFuzzyMatching = allowFuzzyMatching;
        }

        public virtual void Dispose()
        {
            foreach (var kvp in _stringToWordSpans)
            {
                kvp.Value.Dispose();
            }

            _stringToWordSpans.Clear();
        }

        //public bool IsDottedPattern => _dotSeparatedPatternSegments.Length > 1;

        //private bool SkipMatch(string candidate)
            
        public static PatternMatcher CreatePatternMatcher(
            string pattern,
            CultureInfo culture = null,
            bool includeMatchedSpans = false,
            bool allowFuzzyMatching = false)
        {
            return new SimplePatternMatcher(pattern, culture, includeMatchedSpans, allowFuzzyMatching);
        }

        public static PatternMatcher CreateContainerPatternMatcher(
            string[] patternParts,
            char[] containerSplitCharacters,
            CultureInfo culture = null,
            bool allowFuzzyMatching = false)
        {
            return new ContainerPatternMatcher(
                patternParts, containerSplitCharacters, culture, allowFuzzyMatching);
        }

        public static PatternMatcher CreateDotSeparatedContainerMatcher(
            string pattern,
            CultureInfo culture = null,
            bool allowFuzzyMatching = false)
        {
            return CreateContainerPatternMatcher(
                pattern.Split(s_dotCharacterArray, StringSplitOptions.RemoveEmptyEntries),
                s_dotCharacterArray, culture, allowFuzzyMatching);
        }

        internal static (string name, string containerOpt) GetNameAndContainer(string pattern)
        {
            var dotIndex = pattern.LastIndexOf('.');
            var containsDots = dotIndex >= 0;
            return containsDots
                ? (name: pattern.Substring(dotIndex + 1), containerOpt: pattern.Substring(0, dotIndex))
                : (name: pattern, containerOpt: null);
        }

        public abstract bool AddMatches(string candidate, ArrayBuilder<PatternMatch> matches);

        private bool SkipMatch(string candidate)
            => _invalidPattern || string.IsNullOrWhiteSpace(candidate);

        private StringBreaks GetWordSpans(string word)
        {
            lock (_gate)
            {
                return _stringToWordSpans.GetOrAdd(word, _breakIntoWordSpans);
            }
        }

        private static bool ContainsUpperCaseLetter(string pattern)
        {
            // Expansion of "foreach(char ch in pattern)" to avoid a CharEnumerator allocation
            for (int i = 0; i < pattern.Length; i++)
            {
                if (char.IsUpper(pattern[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private PatternMatch? MatchPatternChunk(
            string candidate,
            TextChunk patternChunk,
            bool punctuationStripped,
            bool fuzzyMatch)
        {
            return fuzzyMatch
                ? FuzzyMatchPatternChunk(candidate, patternChunk, punctuationStripped)
                : NonFuzzyMatchPatternChunk(candidate, patternChunk, punctuationStripped);
        }

        private PatternMatch? FuzzyMatchPatternChunk(
            string candidate,
            TextChunk patternChunk,
            bool punctuationStripped)
        {
            if (patternChunk.SimilarityChecker.AreSimilar(candidate))
            {
                return new PatternMatch(
                    PatternMatchKind.Fuzzy, punctuationStripped, isCaseSensitive: false, matchedSpan: null);
            }

            return null;
        }

        private PatternMatch? NonFuzzyMatchPatternChunk(
            string candidate,
            TextChunk patternChunk,
            bool punctuationStripped)
        {
            int caseInsensitiveIndex = _compareInfo.IndexOf(candidate, patternChunk.Text, CompareOptions.IgnoreCase);
            if (caseInsensitiveIndex == 0)
            {
                if (patternChunk.Text.Length == candidate.Length)
                {
                    // a) Check if the part matches the candidate entirely, in an case insensitive or
                    //    sensitive manner.  If it does, return that there was an exact match.
                    return new PatternMatch(
                        PatternMatchKind.Exact, punctuationStripped, isCaseSensitive: candidate == patternChunk.Text,
                        matchedSpan: GetMatchedSpan(0, candidate.Length));
                }
                else
                {
                    // b) Check if the part is a prefix of the candidate, in a case insensitive or sensitive
                    //    manner.  If it does, return that there was a prefix match.
                    return new PatternMatch(
                        PatternMatchKind.Prefix, punctuationStripped, isCaseSensitive: _compareInfo.IsPrefix(candidate, patternChunk.Text),
                        matchedSpan: GetMatchedSpan(0, patternChunk.Text.Length));
                }
            }

            var isLowercase = !ContainsUpperCaseLetter(patternChunk.Text);
            if (isLowercase)
            {
                if (caseInsensitiveIndex > 0)
                {
                    // c) If the part is entirely lowercase, then check if it is contained anywhere in the
                    //    candidate in a case insensitive manner.  If so, return that there was a substring
                    //    match. 
                    //
                    //    Note: We only have a substring match if the lowercase part is prefix match of some
                    //    word part. That way we don't match something like 'Class' when the user types 'a'.
                    //    But we would match 'FooAttribute' (since 'Attribute' starts with 'a').
                    //
                    //    Also, if we matched at location right after punctuation, then this is a good
                    //    substring match.  i.e. if the user is testing mybutton against _myButton
                    //    then this should hit. As we really are finding the match at the beginning of 
                    //    a word.
                    if (char.IsPunctuation(candidate[caseInsensitiveIndex - 1]) ||
                        char.IsPunctuation(patternChunk.Text[0]))
                    {
                        return new PatternMatch(
                            PatternMatchKind.Substring, punctuationStripped,
                            isCaseSensitive: PartStartsWith(
                                candidate, new TextSpan(caseInsensitiveIndex, patternChunk.Text.Length),
                                patternChunk.Text, CompareOptions.None),
                            matchedSpan: GetMatchedSpan(caseInsensitiveIndex, patternChunk.Text.Length));
                    }

                    var wordSpans = GetWordSpans(candidate);
                    for (int i = 0, n = wordSpans.GetCount(); i < n; i++)
                    {
                        var span = wordSpans[i];
                        if (PartStartsWith(candidate, span, patternChunk.Text, CompareOptions.IgnoreCase))
                        {
                            return new PatternMatch(PatternMatchKind.Substring, punctuationStripped,
                                isCaseSensitive: PartStartsWith(candidate, span, patternChunk.Text, CompareOptions.None),
                                matchedSpan: GetMatchedSpan(span.Start, patternChunk.Text.Length));
                        }
                    }
                }
            }
            else
            {
                // d) If the part was not entirely lowercase, then check if it is contained in the
                //    candidate in a case *sensitive* manner. If so, return that there was a substring
                //    match.
                var caseSensitiveIndex = _compareInfo.IndexOf(candidate, patternChunk.Text);
                if (caseSensitiveIndex > 0)
                {
                    return new PatternMatch(
                        PatternMatchKind.Substring, punctuationStripped, isCaseSensitive: true,
                        matchedSpan: GetMatchedSpan(caseSensitiveIndex, patternChunk.Text.Length));
                }
            }

            var match = TryCamelCaseMatch(
                candidate, patternChunk, punctuationStripped, isLowercase);
            if (match.HasValue)
            {
                return match.Value;
            }

            if (isLowercase)
            {
                //   g) The word is all lower case. Is it a case insensitive substring of the candidate
                //      starting on a part boundary of the candidate?

                // We could check every character boundary start of the candidate for the pattern. 
                // However, that's an m * n operation in the worst case. Instead, find the first 
                // instance of the pattern  substring, and see if it starts on a capital letter. 
                // It seems unlikely that the user will try to filter the list based on a substring
                // that starts on a capital letter and also with a lowercase one. (Pattern: fogbar, 
                // Candidate: quuxfogbarFogBar).
                if (patternChunk.Text.Length < candidate.Length)
                {
                    if (caseInsensitiveIndex != -1 && char.IsUpper(candidate[caseInsensitiveIndex]))
                    {
                        return new PatternMatch(
                            PatternMatchKind.Substring, punctuationStripped, isCaseSensitive: false,
                            matchedSpan: GetMatchedSpan(caseInsensitiveIndex, patternChunk.Text.Length));
                    }
                }
            }

            return null;
        }

        private TextSpan? GetMatchedSpan(int start, int length)
            => _includeMatchedSpans ? new TextSpan(start, length) : (TextSpan?)null;

        private static bool ContainsSpaceOrAsterisk(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == ' ' || ch == '*')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Internal helper for MatchPatternInternal
        /// </summary>
        /// <remarks>
        /// PERF: Designed to minimize allocations in common cases.
        /// If there's no match, then null is returned.
        /// If there's a single match, or the caller only wants the first match, then it is returned (as a Nullable)
        /// If there are multiple matches, and the caller wants them all, then a List is allocated.
        /// </remarks>
        /// <param name="candidate">The word being tested.</param>
        /// <param name="segment">The segment of the pattern to check against the candidate.</param>
        /// <param name="matches">The result array to place the matches in.</param>
        /// <param name="fuzzyMatch">If a fuzzy match should be performed</param>
        /// <returns>If there's only one match, then the return value is that match. Otherwise it is null.</returns>
        private bool MatchPatternSegment(
            string candidate,
            PatternSegment segment,
            ArrayBuilder<PatternMatch> matches,
            bool fuzzyMatch)
        {
            if (fuzzyMatch && !_allowFuzzyMatching)
            {
                return false;
            }

            // First check if the segment matches as is.  This is also useful if the segment contains
            // characters we would normally strip when splitting into parts that we also may want to
            // match in the candidate.  For example if the segment is "@int" and the candidate is
            // "@int", then that will show up as an exact match here.
            //
            // Note: if the segment contains a space or an asterisk then we must assume that it's a
            // multi-word segment.
            if (!ContainsSpaceOrAsterisk(segment.TotalTextChunk.Text))
            {
                var match = MatchPatternChunk(
                    candidate, segment.TotalTextChunk, punctuationStripped: false, fuzzyMatch: fuzzyMatch);
                if (match != null)
                {
                    matches.Add(match.Value);
                    return true;
                }
            }

            // The logic for pattern matching is now as follows:
            //
            // 1) Break the segment passed in into words.  Breaking is rather simple and a
            //    good way to think about it that if gives you all the individual alphanumeric words
            //    of the pattern.
            //
            // 2) For each word try to match the word against the candidate value.
            //
            // 3) Matching is as follows:
            //
            //   a) Check if the word matches the candidate entirely, in an case insensitive or
            //    sensitive manner.  If it does, return that there was an exact match.
            //
            //   b) Check if the word is a prefix of the candidate, in a case insensitive or
            //      sensitive manner.  If it does, return that there was a prefix match.
            //
            //   c) If the word is entirely lowercase, then check if it is contained anywhere in the
            //      candidate in a case insensitive manner.  If so, return that there was a substring
            //      match. 
            //
            //      Note: We only have a substring match if the lowercase part is prefix match of
            //      some word part. That way we don't match something like 'Class' when the user
            //      types 'a'. But we would match 'FooAttribute' (since 'Attribute' starts with
            //      'a').
            //
            //   d) If the word was not entirely lowercase, then check if it is contained in the
            //      candidate in a case *sensitive* manner. If so, return that there was a substring
            //      match.
            //
            //   e) If the word was entirely lowercase, then attempt a special lower cased camel cased 
            //      match.  i.e. cofipro would match CodeFixProvider.
            //
            //   f) If the word was not entirely lowercase, then attempt a normal camel cased match.
            //      i.e. CoFiPro would match CodeFixProvider, but CofiPro would not.  
            //
            //   g) The word is all lower case. Is it a case insensitive substring of the candidate starting 
            //      on a part boundary of the candidate?
            //
            // Only if all words have some sort of match is the pattern considered matched.

            var tempMatches = ArrayBuilder<PatternMatch>.GetInstance();

            try
            {
                var subWordTextChunks = segment.SubWordTextChunks;
                foreach (var subWordTextChunk in subWordTextChunks)
                {
                    // Try to match the candidate with this word
                    var result = MatchPatternChunk(
                        candidate, subWordTextChunk, punctuationStripped: true, fuzzyMatch: fuzzyMatch);
                    if (result == null)
                    {
                        return false;
                    }

                    tempMatches.Add(result.Value);
                }

                matches.AddRange(tempMatches);
                return tempMatches.Count > 0;
            }
            finally
            {
                tempMatches.Free();
            }
        }

        private static bool IsWordChar(char ch)
            => char.IsLetterOrDigit(ch) || ch == '_';

        /// <summary>
        /// Do the two 'parts' match? i.e. Does the candidate part start with the pattern part?
        /// </summary>
        /// <param name="candidate">The candidate text</param>
        /// <param name="candidatePart">The span within the <paramref name="candidate"/> text</param>
        /// <param name="pattern">The pattern text</param>
        /// <param name="patternPart">The span within the <paramref name="pattern"/> text</param>
        /// <param name="compareOptions">Options for doing the comparison (case sensitive or not)</param>
        /// <returns>True if the span identified by <paramref name="candidatePart"/> within <paramref name="candidate"/> starts with
        /// the span identified by <paramref name="patternPart"/> within <paramref name="pattern"/>.</returns>
        private bool PartStartsWith(string candidate, TextSpan candidatePart, string pattern, TextSpan patternPart, CompareOptions compareOptions)
        {
            if (patternPart.Length > candidatePart.Length)
            {
                // Pattern part is longer than the candidate part. There can never be a match.
                return false;
            }

            return _compareInfo.Compare(candidate, candidatePart.Start, patternPart.Length, pattern, patternPart.Start, patternPart.Length, compareOptions) == 0;
        }

        /// <summary>
        /// Does the given part start with the given pattern?
        /// </summary>
        /// <param name="candidate">The candidate text</param>
        /// <param name="candidatePart">The span within the <paramref name="candidate"/> text</param>
        /// <param name="pattern">The pattern text</param>
        /// <param name="compareOptions">Options for doing the comparison (case sensitive or not)</param>
        /// <returns>True if the span identified by <paramref name="candidatePart"/> within <paramref name="candidate"/> starts with <paramref name="pattern"/></returns>
        private bool PartStartsWith(string candidate, TextSpan candidatePart, string pattern, CompareOptions compareOptions)
            => PartStartsWith(candidate, candidatePart, pattern, new TextSpan(0, pattern.Length), compareOptions);

        private PatternMatch? TryCamelCaseMatch(
            string candidate, TextChunk patternChunk,
            bool punctuationStripped, bool isLowercase)
        {
            if (isLowercase)
            {
                //   e) If the word was entirely lowercase, then attempt a special lower cased camel cased 
                //      match.  i.e. cofipro would match CodeFixProvider.
                var candidateParts = GetWordSpans(candidate);
                var camelCaseKind = TryAllLowerCamelCaseMatch(
                    candidate, candidateParts, patternChunk, out var matchedSpans);
                if (camelCaseKind.HasValue)
                {
                    return new PatternMatch(
                        camelCaseKind.Value, punctuationStripped, isCaseSensitive: false,
                        matchedSpans: matchedSpans);
                }
            }
            else
            {
                //   f) If the word was not entirely lowercase, then attempt a normal camel cased match.
                //      i.e. CoFiPro would match CodeFixProvider, but CofiPro would not.  
                if (patternChunk.CharacterSpans.GetCount() > 0)
                {
                    var candidateHumps = GetWordSpans(candidate);
                    var camelCaseKind = TryUpperCaseCamelCaseMatch(candidate, candidateHumps, patternChunk, CompareOptions.None, out var matchedSpans);
                    if (camelCaseKind.HasValue)
                    {
                        return new PatternMatch(
                            camelCaseKind.Value, punctuationStripped, isCaseSensitive: true,
                            matchedSpans: matchedSpans);
                    }

                    camelCaseKind = TryUpperCaseCamelCaseMatch(candidate, candidateHumps, patternChunk, CompareOptions.IgnoreCase, out matchedSpans);
                    if (camelCaseKind.HasValue)
                    {
                        return new PatternMatch(
                            camelCaseKind.Value, punctuationStripped, isCaseSensitive: false,
                            matchedSpans: matchedSpans);
                    }
                }
            }

            return null;
        }

        private PatternMatchKind? TryAllLowerCamelCaseMatch(
            string candidate,
            StringBreaks candidateParts,
            TextChunk patternChunk,
            out ImmutableArray<TextSpan> matchedSpans)
        {
            var matcher = new AllLowerCamelCaseMatcher(_includeMatchedSpans, candidate, candidateParts, patternChunk);
            return matcher.TryMatch(out matchedSpans);
        }

        private PatternMatchKind? TryUpperCaseCamelCaseMatch(
            string candidate,
            StringBreaks candidateHumps,
            TextChunk patternChunk,
            CompareOptions compareOption,
            out ImmutableArray<TextSpan> matchedSpans)
        {
            var patternHumps = patternChunk.CharacterSpans;

            // Note: we may have more pattern parts than candidate parts.  This is because multiple
            // pattern parts may match a candidate part.  For example "SiUI" against "SimpleUI".
            // We'll have 3 pattern parts Si/U/I against two candidate parts Simple/UI.  However, U
            // and I will both match in UI. 

            int currentCandidateHump = 0;
            int currentPatternHump = 0;
            int? firstMatch = null;
            bool? contiguous = null;

            var patternHumpCount = patternHumps.GetCount();
            var candidateHumpCount = candidateHumps.GetCount();

            var matchSpans = ArrayBuilder<TextSpan>.GetInstance();
            while (true)
            {
                // Let's consider our termination cases
                if (currentPatternHump == patternHumpCount)
                {
                    Contract.Requires(firstMatch.HasValue);
                    Contract.Requires(contiguous.HasValue);

                    var matchCount = matchSpans.Count;
                    matchedSpans = _includeMatchedSpans
                        ? new NormalizedTextSpanCollection(matchSpans).ToImmutableArray()
                        : ImmutableArray<TextSpan>.Empty;
                    matchSpans.Free();

                    var camelCaseResult = new CamelCaseResult(firstMatch == 0, contiguous.Value, matchCount, null);
                    return GetCamelCaseKind(camelCaseResult, candidateHumps);
                }
                else if (currentCandidateHump == candidateHumpCount)
                {
                    // No match, since we still have more of the pattern to hit
                    matchedSpans = ImmutableArray<TextSpan>.Empty;
                    matchSpans.Free();
                    return null;
                }

                var candidateHump = candidateHumps[currentCandidateHump];
                bool gotOneMatchThisCandidate = false;

                // Consider the case of matching SiUI against SimpleUIElement. The candidate parts
                // will be Simple/UI/Element, and the pattern parts will be Si/U/I.  We'll match 'Si'
                // against 'Simple' first.  Then we'll match 'U' against 'UI'. However, we want to
                // still keep matching pattern parts against that candidate part. 
                for (; currentPatternHump < patternHumpCount; currentPatternHump++)
                {
                    var patternChunkCharacterSpan = patternHumps[currentPatternHump];

                    if (gotOneMatchThisCandidate)
                    {
                        // We've already gotten one pattern part match in this candidate.  We will
                        // only continue trying to consume pattern parts if the last part and this
                        // part are both upper case.  
                        if (!char.IsUpper(patternChunk.Text[patternHumps[currentPatternHump - 1].Start]) ||
                            !char.IsUpper(patternChunk.Text[patternHumps[currentPatternHump].Start]))
                        {
                            break;
                        }
                    }

                    if (!PartStartsWith(candidate, candidateHump, patternChunk.Text, patternChunkCharacterSpan, compareOption))
                    {
                        break;
                    }

                    matchSpans.Add(new TextSpan(candidateHump.Start, patternChunkCharacterSpan.Length));
                    gotOneMatchThisCandidate = true;

                    firstMatch = firstMatch ?? currentCandidateHump;

                    // If we were contiguous, then keep that value.  If we weren't, then keep that
                    // value.  If we don't know, then set the value to 'true' as an initial match is
                    // obviously contiguous.
                    contiguous = contiguous ?? true;

                    candidateHump = new TextSpan(candidateHump.Start + patternChunkCharacterSpan.Length, candidateHump.Length - patternChunkCharacterSpan.Length);
                }

                // Check if we matched anything at all.  If we didn't, then we need to unset the
                // contiguous bit if we currently had it set.
                // If we haven't set the bit yet, then that means we haven't matched anything so
                // far, and we don't want to change that.
                if (!gotOneMatchThisCandidate && contiguous.HasValue)
                {
                    contiguous = false;
                }

                // Move onto the next candidate.
                currentCandidateHump++;
            }
        }
    }
}
