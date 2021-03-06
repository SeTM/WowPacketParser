using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using WowPacketParser.Enums;
using WowPacketParser.Enums.Version;
using WowPacketParser.Misc;

namespace WowPacketParser.Loading
{
    public static class Reader
    {
        public static IPacketReader GetPacketReader(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            if (extension == null)
                throw new IOException("Invalid file type");

            IPacketReader reader;

            switch (extension.ToLower())
            {
                case ".bin":
                    reader = new BinaryPacketReader(SniffType.Bin, fileName, Encoding.ASCII);
                    break;
                case ".pkt":
                    reader = new BinaryPacketReader(SniffType.Pkt, fileName, Encoding.ASCII);
                    break;
                default:
                    throw new IOException(String.Format("Invalid file type {0}", extension.ToLower()));
            }

            return reader;
        }

        public static void Read(string fileName, Action<Tuple<Packet, long, long>> action)
        {
            var reader = GetPacketReader(fileName);

            try
            {
                int packetNum = 0, count = 0;
                while (reader.CanRead())
                {
                    var packet = reader.Read(packetNum, fileName);
                    if (packet == null)
                        continue;

                    if (packetNum == 0)
                    {
                        // determine build version based on date of first packet if not specified otherwise
                        if (ClientVersion.IsUndefined())
                            ClientVersion.SetVersion(packet.Time);
                    }

                    if (packetNum++ < Settings.FilterPacketNumLow)
                    {
                        packet.ClosePacket();
                        continue;
                    }

                    // check for filters
                    var opcodeName = Opcodes.GetOpcodeName(packet.Opcode);

                    var add = true;
                    if (Settings.Filters.Length > 0)
                        add = opcodeName.MatchesFilters(Settings.Filters);
                    // check for ignore filters
                    if (add && Settings.IgnoreFilters.Length > 0)
                        add = !opcodeName.MatchesFilters(Settings.IgnoreFilters);

                    if (add)
                    {
                        action(Tuple.Create(packet, reader.GetCurrentSize(), reader.GetTotalSize()));
                        if (Settings.FilterPacketsNum > 0 && count++ == Settings.FilterPacketsNum)
                            break;
                    }
                    else
                        packet.ClosePacket();

                    if (Settings.FilterPacketNumHigh > 0 && packetNum > Settings.FilterPacketNumHigh)
                        break;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Data);
                Trace.WriteLine(ex.GetType());
                Trace.WriteLine(ex.Message);
                Trace.WriteLine(ex.StackTrace);
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
