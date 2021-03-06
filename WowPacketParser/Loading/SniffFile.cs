﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WowPacketParser.Enums;
using WowPacketParser.Misc;
using WowPacketParser.Parsing;
using WowPacketParser.Saving;
using WowPacketParser.SQL;
using WowPacketParser.Store;

namespace WowPacketParser.Loading
{
    public class SniffFile
    {
        private readonly string _fileName;
        private readonly string _outFileName;
        private readonly Statistics _stats;
        private readonly DumpFormatType _dumpFormat;
        private readonly string _logPrefix;

        private readonly LinkedList<string> _withErrorHeaders = new LinkedList<string>();
        private readonly LinkedList<string> _skippedHeaders = new LinkedList<string>();

        public SniffFile(string fileName, DumpFormatType dumpFormat = DumpFormatType.Text, Tuple<int, int> number = null)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("fileName cannot be null, empty or whitespace.", "fileName");

            _stats = new Statistics();
            _fileName = fileName;
            _dumpFormat = dumpFormat;

            _outFileName = Path.ChangeExtension(fileName, null) + "_parsed.txt";

            if (number == null)
                _logPrefix = string.Format("[{0}]", Path.GetFileName(fileName));
            else
                _logPrefix = string.Format("[{0}/{1} {2}]", number.Item1, number.Item2, Path.GetFileName(fileName));
        }

        public void ProcessFile()
        {
            switch (_dumpFormat)
            {
                case DumpFormatType.StatisticsPreParse:
                {
                    var packets = (LinkedList<Packet>)ReadPackets();
                    if (packets.Count == 0)
                        return;

                    // CSV format:
                    // - sniff file name
                    // - time of first packet
                    // - time of last packet
                    // - sniff duration (seconds)
                    // - packet count
                    // - total packets size (bytes)
                    // - average packet size (bytes)
                    // - smaller packet size (bytes)
                    // - larger packet size (bytes)

                    Trace.WriteLine(String.Format("{0};{1};{2};{3};{4};{5};{6};{7};{8}",
                        _fileName,
                        packets.First.Value.Time,
                        packets.Last.Value.Time,
                        (packets.Last.Value.Time - packets.First.Value.Time).TotalSeconds,
                        packets.Count,
                        packets.AsParallel().Sum(packet => packet.Length),
                        packets.AsParallel().Average(packet => packet.Length),
                        packets.AsParallel().Min(packet => packet.Length),
                        packets.AsParallel().Max(packet => packet.Length)));

                    break;
                }
                case DumpFormatType.SniffDataOnly:
                case DumpFormatType.SqlOnly:
                case DumpFormatType.Text:
                {
                    if (Utilities.FileIsInUse(_outFileName) && Settings.DumpFormat != DumpFormatType.SqlOnly)
                    {
                        // If our dump format requires a .txt to be created,
                        // check if we can write to that .txt before starting parsing
                        Trace.WriteLine(string.Format("Save file {0} is in use, parsing will not be done.", _outFileName));
                        return;
                    }

                    Store.Store.SQLEnabledFlags = Settings.SQLOutputFlag;

                    File.Delete(_outFileName);

                    _stats.SetStartTime(DateTime.Now);

                    using (var writer = (Settings.DumpFormatWithText() ? new StreamWriter(_outFileName, true) : null))
                    {
                        if (writer != null)
                            writer.WriteLine(GetHeader());
                        bool first = true;

                        Reader.Read(_fileName, p =>
                        {
                            var packet = p.Item1;
                            var currSize = p.Item2;
                            var totalSize = p.Item3;

                            if (first)
                            {
                                Trace.WriteLine(string.Format("{0}: Parsing {1} packets. Assumed version {2}",
                                    _logPrefix, Utilities.BytesToString(totalSize), ClientVersion.VersionString));
                                first = false;
                            }

                            ShowPercentProgress("Processing...", currSize, totalSize);

                            // Parse the packet, adding text to Writer and stuff to the stores
                            if (packet.Direction == Direction.BNClientToServer ||
                                packet.Direction == Direction.BNServerToClient)
                                Handler.ParseBattlenet(packet);
                            else
                                Handler.Parse(packet);

                            // Update statistics
                            _stats.AddByStatus(packet.Status);

                            // get packet header if necessary
                            if (Settings.LogPacketErrors)
                            {
                                if (packet.Status == ParsedStatus.WithErrors)
                                    _withErrorHeaders.AddLast(packet.GetHeader());
                                else if (packet.Status == ParsedStatus.NotParsed)
                                    _skippedHeaders.AddLast(packet.GetHeader());
                            }

                            if (writer != null)
                            {
                                // Write to file
                                writer.WriteLine(packet.Writer);
                                writer.Flush();
                            }

                            // Close Writer, Stream - Dispose
                            packet.ClosePacket();
                        });

                        _stats.SetEndTime(DateTime.Now);
                    }

                    Trace.WriteLine(string.Format("{0}: Saved file to '{1}'", _logPrefix, _outFileName));
                    Trace.WriteLine(string.Format("{0}: {1}", _logPrefix, _stats));

                    if (Settings.SQLOutputFlag != 0)
                        WriteSQLs();

                    if (Settings.LogPacketErrors)
                        WritePacketErrors();

                    GC.Collect(); // Force a GC collect after parsing a file. It seems to help.

                    break;
                }
                case DumpFormatType.Pkt:
                {
                    var packets = (LinkedList<Packet>)ReadPackets();
                    if (packets.Count == 0)
                        return;

                    if (Settings.FilterPacketNumLow == 0 && Settings.FilterPacketNumHigh == 0 &&
                        Settings.FilterPacketsNum < 0)
                    {
                        int packetsPerSplit = Math.Abs(Settings.FilterPacketsNum);
                        int totalPackets = packets.Count;

                        int numberOfSplits = totalPackets/packetsPerSplit;

                        for (int i = 0; i < numberOfSplits; ++i)
                        {
                            var fileNamePart = _fileName + "_part_" + (i + 1) + ".pkt";

                            var packetsPart = new LinkedList<Packet>();

                            for (int j = 0; j < packetsPerSplit; ++j)
                            {
                                if (packets.Count == 0)
                                    break;

                                packetsPart.AddLast(packets.First.Value);
                                packets.RemoveFirst();
                            }

                            BinaryDump(fileNamePart, packetsPart);
                        }
                    }
                    else
                    {
                        var fileNameExcerpt = Path.ChangeExtension(_fileName, null) + "_excerpt.pkt";
                        BinaryDump(fileNameExcerpt, packets);
                    }

                    break;
                }
                case DumpFormatType.PktSplit:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        return;

                    SplitBinaryDump(packets);
                    break;
                }
                case DumpFormatType.PktSessionSplit:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        return;

                    SessionSplitBinaryDump(packets);
                    break;
                }
                default:
                {
                    Trace.WriteLine(string.Format("{0}: Dump format is none, nothing will be processed.", _logPrefix));
                    break;
                }
            }
        }

        private string GetHeader()
        {
            return "# TrinityCore - WowPacketParser" + Environment.NewLine +
                   "# File name: " + Path.GetFileName(_fileName) + Environment.NewLine +
                   "# Detected build: " + ClientVersion.Build + Environment.NewLine +
                   "# Parsing date: " + DateTime.Now.ToString(CultureInfo.InvariantCulture) +
                   Environment.NewLine;
        }

        private static long _lastPercent;
        static void ShowPercentProgress(string message, long curr, long total)
        {
            var percent = (100 * curr) / total;
            if (percent == _lastPercent)
                return; // we only need to update if percentage changes otherwise we would be wasting precious resources

            _lastPercent = percent;

            Console.Write("\r{0} {1}% complete", message, percent);
            if (curr == total)
                Console.WriteLine();
        }

        public ICollection<Packet> ReadPackets()
        {
            ICollection<Packet> packets = new LinkedList<Packet>();

            // stats.SetStartTime(DateTime.Now);

            Reader.Read(_fileName, p =>
            {
                var packet = p.Item1;
                var currSize = p.Item2;
                var totalSize = p.Item3;

                ShowPercentProgress("Reading...", currSize, totalSize);
                packets.Add(packet);
            });

            return packets;

            // stats.SetEndTime(DateTime.Now);
            // Trace.WriteLine(string.Format("{0}: {1}", _logPrefix, _stats));
        }

        private void SplitBinaryDump(ICollection<Packet> packets)
        {
            Trace.WriteLine(string.Format("{0}: Splitting {1} packets to multiple files...", _logPrefix, packets.Count));

            try
            {
                SplitBinaryPacketWriter.Write(packets, Encoding.ASCII);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.GetType());
                Trace.WriteLine(ex.Message);
                Trace.WriteLine(ex.StackTrace);
            }
        }

        private void SessionSplitBinaryDump(ICollection<Packet> packets)
        {
            Trace.WriteLine(string.Format("{0}: Splitting {1} packets to multiple files...", _logPrefix, packets.Count));

            try
            {
                SplitSessionBinaryPacketWriter.Write(packets, Encoding.ASCII);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.GetType());
                Trace.WriteLine(ex.Message);
                Trace.WriteLine(ex.StackTrace);
            }
        }

        private void BinaryDump(string fileName, ICollection<Packet> packets)
        {
            Trace.WriteLine(string.Format("{0}: Copying {1} packets to .pkt format...", _logPrefix, packets.Count));

            try
            {
                BinaryPacketWriter.Write(SniffType.Pkt, fileName, Encoding.ASCII, packets);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.GetType());
                Trace.WriteLine(ex.Message);
                Trace.WriteLine(ex.StackTrace);
            }
        }

        private void WriteSQLs()
        {
            string sqlFileName;
            if (String.IsNullOrWhiteSpace(Settings.SQLFileName))
                sqlFileName = string.Format("{0}_{1}.sql",
                    Utilities.FormattedDateTimeForFiles(), Path.GetFileName(_fileName));
            else
                sqlFileName = Settings.SQLFileName;

            if (String.IsNullOrWhiteSpace(Settings.SQLFileName))
            {
                Builder.DumpSQL(string.Format("{0}: Dumping sql", _logPrefix), sqlFileName, GetHeader());
                Storage.ClearContainers();
            }
        }

        private void WritePacketErrors()
        {
            if (_withErrorHeaders.Count == 0 && _skippedHeaders.Count == 0)
                return;

            var fileName = Path.GetFileNameWithoutExtension(_fileName) + "_errors.txt";

            using (var file = new StreamWriter(fileName))
            {
                file.WriteLine(GetHeader());

                if (_withErrorHeaders.Count != 0)
                {
                    file.WriteLine("- Packets with errors:");
                    foreach (var header in _withErrorHeaders)
                        file.WriteLine(header);
                    file.WriteLine();
                }

                if (_skippedHeaders.Count != 0)
                {
                    file.WriteLine("- Packets not parsed:");
                    foreach (var header in _skippedHeaders)
                        file.WriteLine(header);
                }
            }
        }
    }
}
