﻿using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.CST.OpenSource.Shared
{
    public class StringOutputBuilder : IOutputBuilder
    {
        /// <summary> 
        /// Append more text to the result An incompatible object input will result in ArgumentException
        /// exception 
        /// </summary> 
        /// <param name="output">An IEnumerable<string> object</param>
        public void AppendOutput(IEnumerable<object> output)
        {
            lock (stringResults)
            {
                if (output is IEnumerable<string> strings)
                {
                    stringResults.AddRange(strings);
                }
                else
                {
                    throw new ArgumentException("output must be of type IEnumerable<string> when calling this OutputBuilder.");
                }
            }
        }

        public string GetOutput()
        {
            return string.Join(Environment.NewLine, stringResults);
        }

        /// <summary>
        ///     Prints to the console
        /// </summary>
        public void PrintOutput()
        {
            foreach(var result in stringResults)
            {
                Console.Out.WriteLine(result);
            }
        }

        /// <summary>
        ///     Write the output to the given file. Creating directory if needed.
        /// </summary>
        public void WriteOutput(string fileName)
        {
            using var fs = new FileStream(fileName, FileMode.Create, FileAccess.ReadWrite);
            using var sw = new StreamWriter(fs);
            foreach(var result in stringResults)
            {
                sw.WriteLine(result);
            }
        }

        private readonly List<string> stringResults = new();
    }
}