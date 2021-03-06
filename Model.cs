﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace SourceStructureAnalyser
{
    [XmlRoot("SourceStructure")]
    public class Model
    {
        public enum FolderColors
        {
            Normal,
            Grün,
            Orange,
            Rot
        };

        public class FolderInfo
        {
            public string Description { get; set; }

            [XmlAttribute("color")]
            public FolderColors Color { get; set; } = FolderColors.Normal;

            [XmlAttribute("name")]
            public string RelativeName { get; set; }

            [XmlElement("Folder")]
            public readonly List<FolderInfo> Folders = new List<FolderInfo>();

            [XmlElement("File")]
            public readonly List<FileInfo> Files = new List<FileInfo>();

            [XmlAttribute("excluded")]
            public bool IsExcluded { get; set; }

            public IEnumerable<FolderInfo> GetAllFolders() =>
                Folders.Concat(Folders.SelectMany(f => f.Folders));

            public IEnumerable<FileInfo> GetAllFiles() =>
                Files.Concat(Folders.SelectMany(f => f.GetAllFiles()));

            public Tuple<int, int> Export(string dir, StreamWriter writer)
            {
                dir = Path.Combine(dir, RelativeName ?? string.Empty);

                var lines = 0;
                var files = 0;

                foreach (var folder in Folders)
                {
                    var children = folder.Export(dir, writer);

                    files += children.Item1;
                    lines += children.Item2;
                }

                foreach (var file in Files)
                {
                    file.Export(dir, writer);

                    lines += file.NumberOfLines;
                    files += 1;
                }

                writer.WriteLine($"{dir}\t{files:N0}\t{lines:N0}");

                return Tuple.Create(files, lines);
            }

            public bool Scan(string path, HashSet<string> excludedExtensions, CancellationToken cancel)
            {
                if (cancel.IsCancellationRequested)
                    return false;

                var knownFolders = Folders.ToDictionary(f => f.RelativeName, StringComparer.InvariantCultureIgnoreCase);

                foreach (var dir in Directory.GetDirectories(path))
                {
                    var name = Path.GetFileName(dir);

                    FolderInfo folder;
                    if (!knownFolders.TryGetValue(name, out folder))
                    {
                        folder = new FolderInfo { RelativeName = name };

                        knownFolders.Add(name, folder);

                        Folders.Add(folder);
                    }
                    else if (folder.IsExcluded)
                    {
                        folder.Folders.Clear();
                        folder.Files.Clear();

                        continue;
                    }

                    if (!folder.Scan(dir, excludedExtensions, cancel))
                        return false;
                }

                var knownFiles = Files.ToDictionary(f => f.Name, StringComparer.InvariantCultureIgnoreCase);

                foreach (var abs in Directory.GetFiles(path))
                {
                    if (cancel.IsCancellationRequested)
                        return false;

                    var name = Path.GetFileName(abs);
                    var ext = Path.GetExtension(abs);
                    var allow = !excludedExtensions.Contains(ext ?? string.Empty);

                    FileInfo file;
                    if (!knownFiles.TryGetValue(name, out file))
                    {
                        if (!allow)
                            continue;

                        file = new FileInfo { Name = name };

                        knownFiles.Add(name, file);

                        Files.Add(file);
                    }
                    else if (!allow)
                    {
                        knownFiles.Remove(name);

                        Files.Remove(file);
                    }

                    file.Scan(abs);
                }

                return true;
            }
        }

        public class FileInfo
        {
            [XmlAttribute("name")]
            public string Name { get; set; }

            [XmlAttribute("lines")]
            public int NumberOfLines { get; set; }

            [XmlAttribute("excluded")]
            public bool IsExcluded { get; set; }

            public void Scan(string path)
            {
                NumberOfLines = IsExcluded ? 0 : File.ReadAllLines(path).Length;
            }

            public void Export(string dir, StreamWriter writer) =>
                writer.WriteLine($"{Path.Combine(dir, Name)}\t1\t{NumberOfLines:N0}");
        }

        private static readonly XmlSerializer _Serializer = new XmlSerializer(typeof(Model));

        private static readonly XmlWriterSettings _Write = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true };

        public string RootPath { get; set; }

        public string[] ExcludedExtensions { get; set; } = { };

        public FolderInfo RootFolder { get; set; } = new FolderInfo();

        public void Save(Stream stream)
        {
            using (var write = XmlWriter.Create(stream, _Write))
                _Serializer.Serialize(write, this);
        }

        public void Save(string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                Save(stream);
        }

        public void Export(Stream stream)
        {
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine("Pfad\tDateien (aufsummiert)\tZeilen (aufsummiert)");

                RootFolder.Export("$", writer);
            }
        }

        public void Export(string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                Export(stream);
        }

        public static Model Load(Stream stream)
        {
            using (var read = XmlReader.Create(stream))
                return (Model)_Serializer.Deserialize(read);
        }

        public static Model Load(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return Load(stream);
        }

        public Task Scan(CancellationToken cancel) => Task.Run(() => RootFolder.Scan(RootPath, new HashSet<string>(ExcludedExtensions, StringComparer.InvariantCultureIgnoreCase), cancel));
    }
}
