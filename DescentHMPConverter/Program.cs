using LibDescent.Data;
using LibDescent.Data.Midi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DescentHMPConverter
{
    class Program
    {
        static int Main(string[] args)
        {
            PrintIntro();
            int result = ParseArgs(args, out ProgramCall prog);
            if (result != 0) return result;

            switch (prog.Mode)
            {
                case ProgramMode.MIDIToHMP:
                    return ConvertMIDIToHMP(prog);
                case ProgramMode.HMPToMIDI:
                    return ConvertHMPToMIDI(prog);
            }

            PrintHelp();
            return 2;
        }

        static readonly MIDIControl[] forbiddenControls = new MIDIControl[] { 
            MIDIControl.ResetAllControl,
            MIDIControl.LocalControl,
            MIDIControl.OmniModeOff,
            MIDIControl.OmniModeOn,
            MIDIControl.PolyModeOff,
            MIDIControl.PolyModeOn
        };

        private static bool Overwrite(ProgramFlags flags, string outfn)
        {
            if (!FileExists(outfn) || flags.HasFlag(ProgramFlags.Overwrite))
                return true;
            Console.Write(outfn + " already exists. Overwrite (Y/N)?");
            Console.Out.Flush();
            bool? value = null;

            while (!value.HasValue)
            {
                var key = Console.Read();
                if (key < 0)
                    value = false;
                else if (key == 13 || key == 10)
                    value = false;
                else if (key == 'N' || key == 'n')
                    value = false;
                else if (key == 'Y' || key == 'y')
                    value = true;
            }
            Console.WriteLine();
            return value.Value;
        }

        private static int ConvertMIDIToHMP(ProgramCall prog)
        {
            MIDISequence midi;
            try
            {
                midi = MIDISequence.LoadMIDI(ReadFile(prog.SourceFile));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not load MIDI!");
                Console.Error.WriteLine(ex.ToString());
                return 2;
            }
            midi.Convert(MIDIFormat.HMI);

            // ==============================
            // clean up MIDI sequence for HMP
            // ==============================

            // remove tempo changes but hard-apply them onto the track
            midi.NormalizeTempo();

            // set PPQ to 60 and adjust timings
            midi.AdjustPPQ(60);

            // add track volume messages to tracks that don't have them, otherwise they get muted
            foreach (MIDITrack trk in midi.Tracks)
            {
                IEnumerable<MIDIMessage> messages = trk.GetAllEvents().Select(e => e.Data);
                if (!messages.OfType<MIDIControlChangeMessage>().Where(m => m.Controller == MIDIControl.ChannelVolumeMSB).Any())
                {
                    MIDIMessage chm = messages.Where(m => !m.IsExtendedEvent).FirstOrDefault();
                    if (chm != null)
                        trk.AddEvent(new MIDIEvent(0, new MIDIControlChangeMessage(chm.Channel, MIDIControl.ChannelVolumeMSB, 127)), true);
                }
            }
            
            // remove SysEx and meta events, as well as forbidden control change events
            foreach (MIDITrack trk in midi.Tracks)
                trk.RemoveMessages(m => m.IsExtendedEvent || m is MIDIControlChangeMessage cm && forbiddenControls.Contains(cm.Controller));

            try
            {
                if (!Overwrite(prog.Flags, prog.TargetFile))
                    return 0;
                WriteFile(midi, prog.TargetFile);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not save HMP!");
                Console.Error.WriteLine(ex.ToString());
                return 2;
            }

            if (prog.HmqMode != FMMode.None)
            {
                string outHmq = Path.ChangeExtension(prog.TargetFile, ".hmq");

                Console.WriteLine("Preparing HMQ for");
                Console.Write("    ");
                switch (prog.HmqMode)
                {
                    case FMMode.Melodic:
                        Console.Write("MELODIC.BNK    DRUM.BNK");
                        midi.RemapProgram(programMapMelodic);
                        break;
                    case FMMode.Intmelo:
                        Console.Write("INTMELO.BNK    INTDRUM.BNK");
                        midi.RemapProgram(programMapIntmelo);
                        break;
                    case FMMode.Hammelo:
                        Console.Write("HAMMELO.BNK    HAMDRUM.BNK");
                        midi.RemapProgram(programMapHammelo);
                        break;
                    case FMMode.Rickmelo:
                        Console.Write("RICKMELO.BNK   RICKDRUM.BNK");
                        midi.RemapProgram(programMapRickmelo);
                        break;
                    case FMMode.D2melod:
                        Console.Write("D2MELOD.BNK    D2DRUMS.BNK");
                        midi.RemapProgram(programMapD2melod);
                        break;
                }
                Console.WriteLine("");

                try
                {
                    if (!Overwrite(prog.Flags, outHmq))
                        return 0;
                    WriteFile(midi, outHmq);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Could not save HMQ!");
                    Console.Error.WriteLine(ex.ToString());
                    return 2;
                }
            }

            Console.Out.WriteLine("Successfully converted and saved HMP file(s)");
            return 0;
        }

        private static int ConvertHMPToMIDI(ProgramCall prog)
        {
            if (prog.HmqMode != FMMode.None)
            {
                PrintHelp();
                return 2;
            }
            
            MIDISequence midi;
            try
            {
                midi = MIDISequence.LoadHMP(ReadFile(prog.SourceFile));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not load HMP!");
                Console.Error.WriteLine(ex.ToString());
                return 2;
            }

            midi.Convert(MIDIFormat.Type1);
            midi.PulsesPerQuarter = 60;
            midi.AdjustPPQ(480);

            if (prog.Flags.HasFlag(ProgramFlags.FaithfulConversionToMIDI))
            {
                // add track volume 0 to all tracks without them
                // (this is how the original SOS engine works)
                foreach (MIDITrack trk in midi.Tracks)
                {
                    IEnumerable<MIDIMessage> messages = trk.GetAllEvents().Select(e => e.Data);
                    if (!messages.OfType<MIDIControlChangeMessage>().Where(m => m.Controller == MIDIControl.ChannelVolumeMSB).Any())
                    {
                        MIDIMessage chm = messages.Where(m => !m.IsExtendedEvent).FirstOrDefault();
                        if (chm != null)
                            trk.AddEvent(new MIDIEvent(0, new MIDIControlChangeMessage(chm.Channel, MIDIControl.ChannelVolumeMSB, 0)), true);
                    }
                }
            }

            try
            {
                if (!Overwrite(prog.Flags, prog.TargetFile))
                    return 0;
                WriteFile(midi, prog.TargetFile);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not save MIDI!");
                Console.Error.WriteLine(ex.ToString());
                return 2;
            }
            Console.Out.WriteLine("Successfully converted and saved MIDI file");
            return 0;
        }

        private static bool FileExists(string fn)
        {
            return ResolvePath(fn).Exists();
        }

        private static byte[] ReadFile(string fn)
        {
            return ResolvePath(fn).Get();
        }

        private static void WriteFile(MIDISequence midi, string fn)
        {
            ResolvePath(fn).Replace(midi.Write());
        }

        private static IFilePath ResolvePath(string fn)
        {
            string pathFn = Path.GetDirectoryName(fn);
            if (File.Exists(pathFn) && ".hog".Equals(Path.GetExtension(pathFn), StringComparison.InvariantCultureIgnoreCase))
            {
                try
                {
                    return new HOGFilePath(pathFn, Path.GetFileName(fn));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error while opening HOG file!");
                    throw ex;
                }
            }
            return new NormalFilePath(fn);
        }

        static void PrintIntro()
        {
            Console.WriteLine("DescentHMPConverter " + Assembly.GetExecutingAssembly().GetName().Version);
            Console.WriteLine("    Converts between .MIDI files and Descent .HMP/.HMQ files");
            Console.WriteLine("    by ziplantil 2020       powered by LibDescent");
            Console.WriteLine("");
        }

        static void PrintHelp()
        {
            Console.WriteLine("DescentHMPConverter /H [/Y] [/F:mode] midifile hmpfile");
            Console.WriteLine("DescentHMPConverter /M [/Y] [/D] hmpfile midifile");
            Console.WriteLine("");
            Console.WriteLine("  hmpfile     A path to a .HMP or .HMQ file, representing");
            Console.WriteLine("              a HMI MIDI P format file.");
            Console.WriteLine("              Can point to inside a .HOG file.");
            Console.WriteLine("  midifile    A path to a .MID file, representing a standard");
            Console.WriteLine("              MIDI 1.0 file.");
            Console.WriteLine("  /H          Converts a MIDI file into a HMP file.");
            Console.WriteLine("  /M          Converts a HMP file into a MIDI file.");
            Console.WriteLine("  /D          HMP file conversion should be faithful to how the");
            Console.WriteLine("              original HMI DOS driver would play it.");
            Console.WriteLine("  /F          Also converts a .MIDI into a .HMQ for FM.");
            Console.WriteLine("              Only adjusts patch mappings; the .SNG still needs");
            Console.WriteLine("              to contain the correct .BNK references.");
            Console.WriteLine("                1    MELODIC/DRUM       (D1 & D2)");
            Console.WriteLine("                2    INTMELO/INTDRUM    (D1)");
            Console.WriteLine("                3    HAMMELO/HAMDRUM    (D1)");
            Console.WriteLine("                4    RICKMELO/RICKDRUM  (D1)");
            Console.WriteLine("                5    D2MELOD/D2DRUMS         (D2)");
            Console.WriteLine("              (For ideal results, make your own HMQ version");
            Console.WriteLine("               by matching instruments instead of using /F)");
            Console.WriteLine("  /Y          Always overwrites the destination file even if it");
            Console.WriteLine("              already exists.");
            Console.WriteLine("");
        }

        static int ParseArgs(string[] args, out ProgramCall call)
        {
            call = new ProgramCall();
            List<string> normalArgs = new List<string>();
            foreach (string arg in args)
            {
                if (arg.Equals("/?", StringComparison.InvariantCultureIgnoreCase))
                {
                    PrintHelp();
                    return 0;
                }
                else if (arg.Equals("/H", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (call.Mode != ProgramMode.None)
                    {
                        PrintHelp();
                        return 2;
                    }
                    call.Mode = ProgramMode.MIDIToHMP;
                }
                else if (arg.Equals("/M", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (call.Mode != ProgramMode.None)
                    {
                        PrintHelp();
                        return 2;
                    }
                    call.Mode = ProgramMode.HMPToMIDI;
                }
                else if (arg.Equals("/Y", StringComparison.InvariantCultureIgnoreCase))
                {
                    call.Flags |= ProgramFlags.Overwrite;
                }
                else if (arg.Equals("/D", StringComparison.InvariantCultureIgnoreCase))
                {
                    call.Flags |= ProgramFlags.FaithfulConversionToMIDI;
                }
                else if (arg.StartsWith("/F:", StringComparison.InvariantCultureIgnoreCase))
                {
                    try
                    {
                        int mode = int.Parse(arg.Substring(3));
                        if (mode < 1 || mode > 5) throw new ArgumentException();
                        call.HmqMode = (FMMode)mode;
                    }
                    catch (Exception)
                    {
                        PrintHelp();
                        return 2;
                    }
                }
                else if (arg.StartsWith("/", StringComparison.InvariantCultureIgnoreCase))
                {
                    Console.WriteLine("Unknown command flag " + arg);
                    PrintHelp();
                    return 2;
                }
                else
                    normalArgs.Add(arg);
            }

            if (normalArgs.Count != 2)
            {
                PrintHelp();
                return 2;
            }

            call.SourceFile = normalArgs[0];
            call.TargetFile = normalArgs[1];

            return 0;
        }

        internal class ProgramCall
        {
            internal ProgramMode Mode;
            internal ProgramFlags Flags;
            internal FMMode HmqMode;
            internal string SourceFile;
            internal string TargetFile;
        }

        internal enum ProgramMode
        {
            None,
            MIDIToHMP,
            HMPToMIDI
        }

        [Flags]
        internal enum ProgramFlags
        {
            None = 0,
            Overwrite = 1,
            FaithfulConversionToMIDI = 2
        }

        internal enum FMMode
        {
            None,
            Melodic,
            Intmelo,
            Hammelo,
            Rickmelo,
            D2melod
        }

        internal interface IFilePath
        {
            bool Exists();
            byte[] Get();
            void Replace(byte[] data);
        }

        internal class NormalFilePath : IFilePath
        {
            private string path;

            internal NormalFilePath(string path)
            {
                this.path = path;
            }

            public bool Exists()
            {
                return File.Exists(path);
            }

            public byte[] Get()
            {
                return File.ReadAllBytes(path);
            }

            public void Replace(byte[] data)
            {
                File.WriteAllBytes(path, data);
            }
        }

        internal class HOGFilePath : IFilePath
        {
            private static Dictionary<string, HOGFile> hogFileCache = new Dictionary<string, HOGFile>();
            private HOGFile hog;
            private string fileName;

            internal HOGFilePath(string hogPath, string fileName)
            {
                if (!hogFileCache.ContainsKey(hogPath))
                    hogFileCache[hogPath] = new HOGFile(hogPath);
                this.hog = hogFileCache[hogPath];
                this.fileName = fileName;
            }

            public bool Exists()
            {
                return hog.ContainsFile(this.fileName);
            }

            public byte[] Get()
            {
                return hog.GetLumpData(this.fileName);
            }

            public void Replace(byte[] data)
            {
                hog.ReplaceLump(new HOGLump(this.fileName, data));
                hog.Write();
            }

            public void Close()
            {
                hog.Close();
            }
        }

        // *************************************************
        // *************************************************
        //   FM INSTRUCTION REMAPPINGS FOR CREATING .HMQ'S
        // *************************************************
        // *************************************************

        static readonly int[] programMapMelodic = new int[] // melodic/drums already basically GM
        {
            // Melodic 0-127
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            // Percussion 0-127
            0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
            0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
            0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
            0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
            0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
            0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF
        };

        static readonly int[] programMapIntmelo = new int[] // TODO
        {
            // Melodic 0-127
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            // Percussion 0-127
            0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
            0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
            0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
            0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
            0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
            0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF
        };

        static readonly int[] programMapHammelo = new int[] // TODO
        {
            // Melodic 0-127
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            // Percussion 0-127
            0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
            0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
            0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
            0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
            0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
            0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF
        };

        static readonly int[] programMapRickmelo = new int[] // TODO
        {
            // Melodic 0-127
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            // Percussion 0-127
            0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
            0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
            0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
            0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
            0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
            0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF
        };

        static readonly int[] programMapD2melod = new int[] // TODO
        {
            // Melodic 0-127
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            // Percussion 0-127
            0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
            0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
            0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
            0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
            0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
            0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF
        };
    }
}
