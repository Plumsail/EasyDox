﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;

namespace EasyDox
{
    public static class Xlsx
    {
        /// <summary>
        /// Merges <paramref name="fieldValues"/> into the xlsx template specified 
        /// by <paramref name="xlsxPath"/> and replaces the original file.
        /// </summary>
        /// <param name="engine">Expression evaluation engine.</param>
        /// <param name="xlsxPath">Template and output path.</param>
        /// <param name="fieldValues">A dictionary of field values keyed by field name.</param>
        /// <returns></returns>
        public static IEnumerable<IMergeError> MergeInplace(Engine engine, string xlsxPath,
            Dictionary<string, string> fieldValues)
        {
            using (var pkg = Package.Open(xlsxPath, FileMode.Open, FileAccess.ReadWrite))
            {
                // Specify the URI of the part to be read
                PackagePart part = pkg.GetPart(new Uri("/xl/sharedStrings.xml", UriKind.Relative));

                var sheetParts = pkg.GetParts()
                    .Where(p =>
                        p.Uri.OriginalString.StartsWith("/xl/worksheets") &&
                        p.Uri.OriginalString.EndsWith(".xml"))
                    .ToList();

                var sheetDocList = new List<XmlDocument>();
                foreach (var sheet in sheetParts)
                {
                    var sheetDoc = new XmlDocument();
                    using (var partStream = sheet.GetStream(FileMode.Open, FileAccess.Read))
                    {
                        sheetDoc.Load(partStream);
                        sheetDocList.Add(sheetDoc);
                    }
                }

                // Get the document part from the package.
                // Load the XML in the part into an XmlDocument instance.
                var sharedStringsDoc = new XmlDocument();
                using (var partStream = part.GetStream(FileMode.Open, FileAccess.Read))
                {
                    sharedStringsDoc.Load(partStream);
                }

                var fields = ReplaceMergeFieldsAndReturnMissingFieldNames(sharedStringsDoc, sheetDocList, fieldValues, engine);

                SaveDocPart(part, sharedStringsDoc);
                int idx = 0;
                foreach (var sheet in sheetParts)
                {
                    SaveDocPart(sheet, sheetDocList[idx]);
                    ++idx;
                }

                return fields;
            }
        }

        private static void SaveDocPart(PackagePart part, XmlDocument xmlDoc)
        {
            using (var stream = part.GetStream(FileMode.Open, FileAccess.Write))
            using (var partWrt = new StreamWriter(stream))
            {
                xmlDoc.Save(partWrt);
            }
        }

        class MergeError : IMergeError
        {
            private readonly Func<IMergeErrorVisitor, string> callback;

            public MergeError(Func<IMergeErrorVisitor, string> callback)
            {
                this.callback = callback;
            }

            public string Accept(IMergeErrorVisitor visitor)
            {
                return callback(visitor);
            }
        }

        public static IEnumerable<IMergeError> ReplaceMergeFieldsAndReturnMissingFieldNames(
            XmlDocument sharedStringsDoc, List<XmlDocument> sheetDocList, Dictionary<string, string> replacements,
            Engine engine)
        {
            var fields = GetSharedStrings(sharedStringsDoc);

            var errors = new List<IMergeError>();

            var properties = new Properties(replacements);

            int stringIdx = 0;
            foreach (var field in fields)
            {
                var value = field.Text;
                var matches = regex.Match(field.Text);

                if (matches.Success)
                {
                    var fieldNames = matches.Groups["name"].Captures;
                    var fieldTemplates = matches.Groups["template"].Captures;

                    for (var fieldIdx = 0; fieldIdx < fieldNames.Count; ++fieldIdx)
                    {
                        var fieldName = fieldNames[fieldIdx].ToString();
                        var fieldTemlate = fieldTemplates[fieldIdx].ToString();

                        var exp = engine.Parse(fieldName);

                        if (exp == null)
                        {
                            errors.Add(new MergeError(v => v.InvalidExpression(fieldName)));
                        }
                        else
                        {
                            var missingProperties = new List<string>();
                            properties.FindMissingProperties(exp, missingProperties);
                            errors.AddRange(missingProperties.Select(p => new MergeError(v => v.MissingField(p))));

                            if (missingProperties.Count == 0) // otherwise Eval will throw
                            {
                                value = value.Replace(fieldTemlate, properties.Eval(exp));
                            }
                        }
                    }

                    if (value != field.Text)
                    {
                        if (double.TryParse(value.Trim(), out double dresult))
                        {
                            foreach (var sheetDoc in sheetDocList)
                            {
                                var cells = GetStringCells(sheetDoc, stringIdx);
                                foreach (var cell in cells)
                                {
                                    cell.DoubleValue = dresult;
                                }
                            }

                            value = "";
                        }
                    }

                    field.StringValue = value;
                }

                ++stringIdx;
            }

            return errors;
        }

        internal static IEnumerable<ICell> GetStringCells(XmlDocument xdoc, int stringIdx)
        {
            var nsManager = new XmlNamespaceManager(new NameTable());
            nsManager.AddNamespace("d", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

            XPathNavigator xDocNavigator = xdoc.CreateNavigator();
            //var nodePath = String.Format("//s:worksheet/s:sheetData/s:row/s:cr[s:@t='s'/s:v={0}]", stringIdx);
            var nodePath = $"//d:sheetData/d:row/d:c[@t='s' and d:v='{stringIdx}']";
            var nodes = xDocNavigator.Select(nodePath, nsManager);

            foreach (XPathNavigator navigator in nodes)
            {
                yield return new SimpleCell(navigator, nsManager);
            }
        }

        internal static IEnumerable<ISharedString> GetSharedStrings(XmlDocument xdoc)
        {
            var nsManager = new XmlNamespaceManager(new NameTable());
            nsManager.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

            XPathNavigator xDocNavigator = xdoc.CreateNavigator();

            var nodes = xDocNavigator.Select("//s:sst/s:si/s:t", nsManager);

            foreach (XPathNavigator navigator in nodes)
            {
                yield return new SimpleSharedString(navigator);
            }
        }

        internal static readonly Regex regex = new Regex(

            @"^(.*?(?<template>\[\[[\s]*(((?<name>.+?(?=[\s]*\]\])))[\s]*)\]\]))+",

            RegexOptions.Compiled
            | RegexOptions.CultureInvariant
            | RegexOptions.ExplicitCapture
            | RegexOptions.IgnoreCase
            | RegexOptions.IgnorePatternWhitespace
            | RegexOptions.Singleline);

        internal interface ISharedString
        {
            string Text { get; }
            string StringValue { get; set; }
        }

        internal class SimpleSharedString : ISharedString
        {
            private readonly XPathNavigator node;

            public SimpleSharedString(XPathNavigator node)
            {
                this.node = node;
            }

            string ISharedString.Text => node.InnerXml;

            string ISharedString.StringValue
            {
                get => node.Value;
                set => node.SetValue(value);
            }
        }

        internal interface ICell
        {
            double DoubleValue { get; set; }
        }

        internal class SimpleCell : ICell
        {
            private readonly XPathNavigator node;
            private readonly XmlNamespaceManager namespaceManager;

            public SimpleCell(XPathNavigator node, XmlNamespaceManager namespaceManager)
            {
                this.node = node;
                this.namespaceManager = namespaceManager;
            }

            double ICell.DoubleValue
            {
                get
                {
                    var child = node.Select("d:v", namespaceManager).Cast<XPathNavigator>().Single();

                    return double.Parse(child.Value);
                }
                set
                {
                    XmlNode curNode = ((IHasXmlNode) node).GetNode();
                    XmlAttribute attrib = curNode?.Attributes?["t"];
                    if (attrib != null)
                    {
                        curNode.Attributes.Remove(attrib);
                    }

                    var child = node.Select("d:v", namespaceManager).Cast<XPathNavigator>().Single();
                    child.SetValue(value.ToString("G17"));
                }
            }
        }
    }
}
