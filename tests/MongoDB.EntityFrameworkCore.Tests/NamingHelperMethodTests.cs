﻿/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Globalization;

namespace MongoDB.EntityFrameworkCore.Tests;

public static class NamingHelperMethodTests
{
    [Theory]
    [InlineData("THIS_word", "thisWord")]
    [InlineData("ThatWord", "thatWord")]
    [InlineData("a-lot-of-wordsHere", "aLotOfWordsHere")]
    [InlineData("TESTING", "testing")]
    [InlineData("TESTING123a", "testing123A")]
    [InlineData("Η θήκη της καμήλας είναι ΚΑΛΗ", "ηΘήκηΤηςΚαμήλαςΕίναιΚαλη")]
    public static void ToCamelCase_finds_word_boundaries_and_camel_cases_words(string toConvert, string expected)
    {
        string actual = toConvert.ToCamelCase(CultureInfo.InvariantCulture);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("THIS_word", "ThisWord")]
    [InlineData("ThatWord", "ThatWord")]
    [InlineData("a-lot-of-wordsHere", "ALotOfWordsHere")]
    [InlineData("TESTING", "Testing")]
    [InlineData("TESTING123a", "Testing123A")]
    [InlineData("Η θήκη της καμήλας είναι ΚΑΛΗ", "ΗΘήκηΤηςΚαμήλαςΕίναιΚαλη")]
    public static void ToTitleCase_finds_word_boundaries_and_title_cases_words(string toConvert, string expected)
    {
        string actual = toConvert.ToTitleCase(CultureInfo.InvariantCulture);
        Assert.Equal(expected, actual);
    }
}
