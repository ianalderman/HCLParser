﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Sprache;

namespace Octopus.CoreParsers.Hcl
{
    /// <summary>
    /// A Sprache parser for the HCL library.
    /// 
    /// The goal of this parser is to have no false negatives. Every valid HCL file should be
    /// parsed by the Parsers in this class.
    /// 
    /// It it very likely that these parsers will parse templates that are not valid HCL. This
    /// is OK though, as the terraform exe will ultimately be the source of truth.
    /// </summary>
    public class HclParser
    {
        /// <summary>
        /// The \n char
        /// </summary>
        public const char LineBreak = (char) 10;

        /// <summary>
        /// A regex used to normalize line endings
        /// </summary>
        public static readonly Regex LineEndNormalize = new Regex("\r\n?|\n");

        /// <summary>
        /// A regex that matches the numbers that can be assigned to a property
        /// matches floats, scientific and hex numbers
        /// </summary>
        public static readonly Regex NumberRegex = new Regex(@"-?(0x)?\d+(e(\+|-)?\d+)?(\.\d*(e(\+|-)?\d+)?)?");

        /// <summary>
        /// A regex that matches true and false
        /// </summary>
        public static readonly Regex TrueFalse = new Regex(@"true|false", RegexOptions.IgnoreCase);

        /// <summary>
        /// An escaped interpolation curly
        /// </summary>
        public static readonly Parser<string> EscapedDelimiterStartCurly = Parse.String("{{").Text().Named("Escaped delimiter");

        /// <summary>
        /// An escaped interpolation curly
        /// </summary>
        public static readonly Parser<string> EscapedDelimiterEndCurly = Parse.String("}}").Text().Named("Escaped delimiter");

        /// <summary>
        /// The start of an interpolation marker
        /// </summary>
        public static readonly Parser<string> DelimiterStartInterpolated = Parse.String("${").Text().Named("Start Interpolation");

        /// <summary>
        /// Special interpolation char
        /// </summary>
        public static readonly Parser<char> DelimiterStartCurly = Parse.Char('{').Named("Delimiter");

        /// <summary>
        /// Special interpolation char
        /// </summary>
        public static readonly Parser<char> DelimiterEndCurly = Parse.Char('}').Named("Delimiter");

        /// <summary>
        /// Escaped quote
        /// </summary>
        public static readonly Parser<string> EscapedDelimiterQuote = Parse.String("\\\"").Text().Named("Escaped delimiter");

        /// <summary>
        /// Escape char
        /// </summary>
        public static readonly Parser<string> SingleEscapeQuote = Parse.String("\\").Text().Named("Single escape character");

        /// <summary>
        /// Double escape
        /// </summary>
        public static readonly Parser<string> DoubleEscapeQuote = Parse.String("\\\\").Text().Named("Escaped escape character");

        /// <summary>
        /// Quote char
        /// </summary>
        public static readonly Parser<char> DelimiterQuote = Parse.Char('"').Named("Delimiter");

        /// <summary>
        /// Start of interpolation
        /// </summary>
        public static readonly Parser<char> DelimiterInterpolation = Parse.Char('$').Named("Interpolated");

        /// <summary>
        /// An escaped interpolation start
        /// </summary>
        public static readonly Parser<string> EscapedDelimiterInterpolation = Parse.Char('$').Repeat(2).Text().Named("Escaped Interpolated");

        /// <summary>
        /// An escaped interpolation start
        /// </summary>
        public static readonly Parser<string> DoubleEscapedDelimiterInterpolation = Parse.Char('$').Repeat(4).Text().Named("Escaped Interpolated");

        /// <summary>
        /// A section of a string that does not have any special interpolation tokens
        /// </summary>
        public static readonly Parser<string> SimpleLiteralCurly =
            Parse.AnyChar
                .Except(EscapedDelimiterStartCurly)
                .Except(DelimiterStartInterpolated)
                .Except(DelimiterEndCurly)
                .Many().Text().Named("Literal without escape/delimiter character");

        /// <summary>
        /// A string made up of regular text and interoplation string
        /// </summary>
        public static readonly Parser<string> StringLiteralCurly =
            from start in DelimiterStartInterpolated
            from v in StringLiteralCurly
                .Or(EscapedDelimiterStartCurly)
                .Or(EscapedDelimiterEndCurly)
                .Or(SimpleLiteralCurly).Many()
            from end in DelimiterEndCurly
            select string.Concat(start) + string.Concat(v) + string.Concat(end);

        public static readonly Parser<string> SimpleLiteralQuote = Parse.AnyChar
            .Except(SingleEscapeQuote)
            .Except(DelimiterQuote)
            .Except(EscapedDelimiterStartCurly)
            .Except(DelimiterStartInterpolated)
            .Many().Text().Named("Literal without escape/delimiter character");

        /// <summary>
        /// Matches the plain text in a string, or the Interpolation block 
        /// </summary>
        public static readonly Parser<string> StringLiteralQuoteContent =
            from curly in StringLiteralCurly.Optional()
            from content in EscapedDelimiterQuote
                .Or(DoubleEscapeQuote)
                .Or(SingleEscapeQuote)
                .Or(EscapedDelimiterInterpolation)
                .Or(DoubleEscapedDelimiterInterpolation)
                .Or(SimpleLiteralQuote).Many()
            select curly.GetOrDefault() + Regex.Unescape(string.Concat(content));

        /// <summary>
        /// Matches the plain text in a string, or the Interpolation block 
        /// </summary>
        public static readonly Parser<string> StringLiteralQuoteContentReverse =
            from combined in (
                from curly in StringLiteralCurly.Optional()
                from content in 
                    EscapedDelimiterInterpolation
                    .Or(Parse.AnyChar.Except(DelimiterStartInterpolated).Many().Text())
                select curly.GetOrDefault() + EscapeString(content)).Many()
            select string.Concat(combined);

        /// <summary>
        /// Matches the plain text in a string, or the Interpolation block 
        /// </summary>
        public static readonly Parser<string> StringLiteralQuoteContentNoInterpolation =
            from content in StringLiteralCurly
                .Or(EscapedDelimiterQuote)
                .Or(DoubleEscapeQuote)
                .Or(SingleEscapeQuote)
                .Or(EscapedDelimiterInterpolation)
                .Or(DoubleEscapedDelimiterInterpolation)
                .Or(SimpleLiteralQuote).Many()
            select string.Concat(content);

        /// <summary>
        /// Matches multiple StringLiteralQuoteContent to make up the string
        /// </summary>
        public static readonly Parser<string> StringLiteralQuote =
        (from start in DelimiterQuote
            from content in StringLiteralQuoteContent.Many()
            from end in DelimiterQuote
            select string.Concat(content)
        ).Token();

        /// <summary>
        /// Represents a multiline comment e.g.
        /// /*
        /// Some text goes here
        /// */
        /// </summary>
        public static readonly Parser<HclElement> MultilineComment =
        (from open in Parse.String("/*")
            from content in Parse.AnyChar.Except(Parse.String("*/"))
                .Or(Parse.Char(LineBreak))
                .Many().Text()
            from last in Parse.String("*/")
            select new HclMultiLineCommentElement {Value = content}).Token().Named("Multiline Comment");

        /// <summary><![CDATA[
        /// Represents a HereDoc e.g.
        /// 
        /// <<EOF
        /// Some Text
        /// Goes here
        /// EOF
        /// 
        /// or
        /// 
        /// <<-EOF
        ///   Some Text
        ///   Goes here
        /// EOF
        /// ]]></summary>
        public static readonly Parser<Tuple<string, bool, string>> HereDoc =
        (from open in Parse.Char('<').Repeat(2).Text()
            from indentMarker in Parse.Char('-').Optional()
            from marker in Parse.AnyChar.Except(Parse.Char(LineBreak)).Many().Text()
            from lineBreak in Parse.Char(LineBreak)
            from rest in Parse.AnyChar.Except(Parse.String(marker))
                .Or(Parse.Char(LineBreak))
                .Many().Text()
            from last in Parse.String(marker)
            select Tuple.Create(marker, indentMarker.IsDefined, lineBreak + rest)).Token();

        /// <summary>
        /// Represents the "//" used to start a comment
        /// </summary>
        public static readonly Parser<IEnumerable<char>> ForwardSlashCommentStart =
        (
            from open in Parse.Char('/').Repeat(2)
            select open
        );

        /// <summary>
        /// Represents the "#" used to start a comment
        /// </summary>
        public static readonly Parser<IEnumerable<char>> HashCommentStart =
        (
            from open in Parse.Char('#').Once()
            select open
        );

        /// <summary>
        /// Represents a single line comment
        /// </summary>
        public static readonly Parser<HclElement> SingleLineComment =
        (
            from open in ForwardSlashCommentStart.Or(HashCommentStart)
            from content in Parse.AnyChar.Except(Parse.Char(LineBreak)).Many().Text().Optional()
            select new HclCommentElement {Value = content.GetOrDefault()}
        ).Token().Named("Single line comment");

        /// <summary>
        /// Represents the identifiers that are used for names, values and types.
        /// </summary>
        public static readonly Parser<string> Identifier =
            Parse.Regex(@"(\d|\w|[_\-.])+").Text().Token();

        /// <summary>
        /// Represents the identifiers that are used for property names
        /// According to https://github.com/hashicorp/hcl/blob/ae25c981c128d7a7a5241e3b7d7409089355df69/hcl/scanner/scanner_test.go
        /// all strings are double quoted.
        /// </summary>
        public static readonly Parser<string> PropertyIdentifier =
            Identifier.Or(StringLiteralQuote).Token();

        /// <summary>
        /// Represents quoted text
        /// </summary>
        public static readonly Parser<string> QuotedText =
        (from open in DelimiterQuote
            from content in Parse.CharExcept('"').Many().Text()
            from close in DelimiterQuote
            select content).Token();

        /// <summary>
        /// Represents the various values that can be assigned to properties
        /// i.e. quoted text, numbers and booleans
        /// </summary>
        public static readonly Parser<string> PropertyValue =
        (from value in StringLiteralQuote
                .Or(Parse.Regex(NumberRegex).Text())
                .Or(Parse.Regex(TrueFalse).Text())
            select value).Token();

        /// <summary>
        /// The value of an individual item in a list
        /// </summary>
        public static readonly Parser<HclElement> LiteralListValue =
            from literal in PropertyValue
            select new HclStringElement {Value = literal};

        /// <summary>
        /// The value of an individual heredoc item in a list
        /// </summary>
        public static readonly Parser<HclElement> HereDocListValue =
            from hereDoc in HereDoc
            select new HclHereDocElement
            {
                Marker = hereDoc.Item1,
                Trimmed = hereDoc.Item2,
                Value = hereDoc.Item3
            };

        /// <summary>
        /// Array start token
        /// </summary>
        public static readonly Parser<char> LeftBracker = Parse.Char('[').Token();

        /// <summary>
        /// Array end token
        /// </summary>
        public static readonly Parser<char> RightBracker = Parse.Char(']').Token();

        /// <summary>
        /// Object start token
        /// </summary>
        public static readonly Parser<char> LeftCurly = Parse.Char('{').Token();

        /// <summary>
        /// Object end token
        /// </summary>
        public static readonly Parser<char> RightCurly = Parse.Char('}').Token();

        /// <summary>
        /// Comma token
        /// </summary>
        public static readonly Parser<char> Comma = Parse.Char(',').Token();

        /// <summary>
        /// Represets the contents of a map
        /// </summary>
        public static readonly Parser<HclElement> MapValue =
        (
            from lbracket in LeftCurly
            from content in HclProperties.Optional()
            from rbracket in RightCurly
            select new HclMapElement {Children = content.GetOrDefault()}
        ).Token();

        /// <summary>
        /// Represents a list. Lists can be embedded.
        /// </summary>
        public static readonly Parser<HclElement> ListValue =
        (
            from open in LeftBracker
            from content in
            (
                from embeddedValues in ListValue
                    .Or(MapValue)
                    .Or(LiteralListValue)
                    .Or(HereDocListValue)
                    .Or(SingleLineComment)
                    .Or(MultilineComment)
                from comma in Comma.Optional()
                select embeddedValues
            ).Token().Many()
            from close in RightBracker
            select new HclListElement {Children = content}
        ).Token();

        /// <summary>
        /// Represets the equals token
        /// </summary>
        public static readonly Parser<char> Equal =
        (
            from equal in Parse.Char('=')
            select equal
        ).Token();

        /// <summary>
        /// Represents a value that can be assigned to a property
        /// </summary>
        public static readonly Parser<HclElement> HclElementProperty =
            from name in Identifier
            from eql in Equal
            from value in PropertyValue
            select new HclStringPropertyElement {Name = name, Value = value, NameQuoted = false};
        
        /// <summary>
        /// Represents a value that can be assigned to a property
        /// </summary>
        public static readonly Parser<HclElement> QuotedHclElementProperty =
            from name in StringLiteralQuote
            from eql in Equal
            from value in PropertyValue
            select new HclStringPropertyElement {Name = name, Value = value, NameQuoted = true};        

        /// <summary>
        /// Represents a multiline string
        /// </summary>
        public static readonly Parser<HclElement> HclElementMultilineProperty =
            from name in Identifier
            from eql in Equal
            from value in HereDoc
            select new HclHereDocPropertyElement
            {
                Name = name,
                NameQuoted = false,
                Marker = value.Item1,
                Trimmed = value.Item2,
                Value = value.Item3
            };
        
        /// <summary>
        /// Represents a multiline string
        /// </summary>
        public static readonly Parser<HclElement> QuotedHclElementMultilineProperty =
            from name in StringLiteralQuote
            from eql in Equal
            from value in HereDoc
            select new HclHereDocPropertyElement
            {
                Name = name,
                NameQuoted = true,
                Marker = value.Item1,
                Trimmed = value.Item2,
                Value = value.Item3
            };        

        /// <summary>
        /// Represents a list property
        /// </summary>
        public static readonly Parser<HclElement> HclElementListProperty =
            from name in Identifier.Or(StringLiteralQuote)
            from eql in Equal
            from value in ListValue
            select new HclListPropertyElement {Name = name, Children = value.Children, NameQuoted = false};
        
        /// <summary>
        /// Represents a list property
        /// </summary>
        public static readonly Parser<HclElement> QuotedHclElementListProperty =
            from name in StringLiteralQuote
            from eql in Equal
            from value in ListValue
            select new HclListPropertyElement {Name = name, Children = value.Children, NameQuoted = true};

        /// <summary>
        /// Represent a map assigned to a named value
        /// </summary>
        public static readonly Parser<HclElement> HclElementMapProperty =
            from name in Identifier.Or(StringLiteralQuote)
            from eql in Equal
            from properties in MapValue
            select new HclMapPropertyElement {Name = name, Children = properties.Children};

        /// <summary>
        /// Represents a named element with child properties
        /// </summary>
        public static readonly Parser<HclElement> HclNameElement =
            from name in Identifier.Or(StringLiteralQuote)
            from lbracket in LeftCurly
            from properties in HclProperties.Optional()
            from rbracket in RightCurly
            select new HclElement {Name = name, Children = properties.GetOrDefault()};

        /// <summary>
        /// Represents a named element with a value and child properties
        /// </summary>
        public static readonly Parser<HclElement> HclNameValueElement =
            from name in Identifier
            from eql in Equal.Optional()
            from value in Identifier.Or(StringLiteralQuote)
            from lbracket in LeftCurly
            from properties in HclProperties.Optional()
            from rbracket in RightCurly
            select new HclElement {Name = name, Value = value, Children = properties.GetOrDefault()};

        /// <summary>
        /// Represents named elements with values and types. These are things like resources.
        /// </summary>
        public static readonly Parser<HclElement> HclNameValueTypeElement =
            from name in Identifier
            from value in Identifier.Or(StringLiteralQuote)
            from type in Identifier.Or(StringLiteralQuote)
            from lbracket in LeftCurly
            from properties in HclProperties.Optional()
            from rbracket in RightCurly
            select new HclElement {Name = name, Value = value, Type = type, Children = properties.GetOrDefault()};

        /// <summary>
        /// Represents the properties that can be added to an element
        /// </summary>
        public static readonly Parser<IEnumerable<HclElement>> HclProperties =
        (from value in HclNameElement
                .Or(HclNameValueElement)
                .Or(HclNameValueTypeElement)
                .Or(HclElementProperty)
                .Or(QuotedHclElementProperty)
                .Or(HclElementListProperty)
                .Or(QuotedHclElementListProperty)
                .Or(HclElementMapProperty)
                .Or(HclElementMultilineProperty)
                .Or(QuotedHclElementMultilineProperty)
                .Or(SingleLineComment)
                .Or(MultilineComment)
            from comma in Comma.Optional()
            select value).Many().Token();

        /// <summary>
        /// The top level document. If you are parsing a HCL file, this is the Parser to use.
        /// This is just a collection of child objects.
        /// </summary>
        public static readonly Parser<HclElement> HclTemplate =
            from children in HclProperties.End()
            select new HclRootElement {Children = children};

        /// <summary>
        /// Replace line breaks with the Unix style line breaks
        /// </summary>
        /// <param name="template">The text to normalize</param>
        /// <returns>The text with normalized line breaks</returns>
        public static string NormalizeLineEndings(string template) =>
            LineEndNormalize.Replace(template, "\n");

        public static string EscapeString(string template) => template
            .Replace("\\", "\\\\")
            .Replace("\n", "\\n")
            .Replace("\a", "\\a")
            .Replace("\b", "\\b")
            .Replace("\f", "\\f")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t")
            .Replace("\v", "\\v")
            .Replace("\"", "\\\"");

    }
}