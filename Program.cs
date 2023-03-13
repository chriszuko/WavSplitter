using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WavCutter
{
	class Program
	{
		private static string _noteNamePrefix = "Note";
		private static string _fullFilePath = "filePath";

		// Binary file format as described here: http://soundfile.sapp.org/doc/WaveFormat/
		static void Main(string[] args)
		{
			if (args.Length == 0)
				quitWithError("ERROR: no file path argument received");

			consoleWrite($"WavCutter called with argument: '{args[0]}'");
			string folder = Path.GetDirectoryName(args[0]) + '/';
			string inputFileName = Path.GetFileName(args[0]);
			_noteNamePrefix = Path.GetFileNameWithoutExtension(args[0]);
			_fullFilePath = folder + inputFileName;
			consoleWrite($"folder: '{folder}'");
			consoleWrite($"inputFileName: '{inputFileName}'");
			consoleWrite($"fullFilePath: '{_fullFilePath}'");

			int[] left;
			int[] right;
			int sampleRate;
			int headerSize;
			int bytesPerSample;
			byte[] wav = File.ReadAllBytes(_fullFilePath);
			deletePreviousNotes(folder);
			openWav(ref wav, out left, out right, out sampleRate, out headerSize, out bytesPerSample);
			cutAndSave(ref wav, left, right, sampleRate, headerSize, bytesPerSample, folder);
			consoleWrite("WavCutter is done");
			Console.ReadKey();
		}


		static int readSample(byte[] data, int pos, int bytesPerSample)
		{
			// Convert 3 bytes to int32, taking into account sign.
			// Solution taken from here: https://stackoverflow.com/questions/8104343/converting-3-bytes-into-signed-integer-in-c-sharp

			byte b0 = 0xff;
			byte b1 = data[pos + 2];
			byte b2 = data[pos + 1];
			byte b3 = data[pos];

			int result = 0;
			if ((b1 & 0x80) != 0)
				result |= b0 << 24;
			result |= b1 << 16;
			result |= b2 << 8;
			result |= b3;
			return result;
		}


		static void writeSample(int value, ref byte[] target, int bytesPerSample, int pos, float volumeMultiplier)
		{
			switch (bytesPerSample)
			{
				case 2:
					value = 0; // 16 bit currently not supported
					break;
				case 3:
				{
					value = (int)(value * volumeMultiplier);
					byte[] valueAsBytes = System.BitConverter.GetBytes(value);
					target[pos]     = valueAsBytes[0];
					target[pos + 1] = valueAsBytes[1];
					target[pos + 2] = valueAsBytes[2];
					break;
				}
			}			
		}


		static void deletePreviousNotes(string folder)
		{
			string[] filePaths = Directory.GetFiles(folder);
			foreach (string filePath in filePaths)
			{
				if (filePath.Contains("/" + _noteNamePrefix) && (filePath != _fullFilePath))
				{
					consoleWrite($"Deleting: {filePath}");
					File.Delete(filePath);
				}
			}
		}


		// Returns left and right double arrays. 'right' will be null if sound is mono.
		static void openWav(ref byte[] wav, out int[] left, out int[] right, out int sampleRate, out int headerSize, out int bytesPerSample)
		{
			// Determine if mono or stereo
			int channels = wav[22];     // Forget byte 23 as 99.999% of WAVs are 1 or 2 channels
			bytesPerSample = wav[34] / 8;

			sampleRate = wav[24] | (wav[25] << 8) | (wav[26] << 16) | (wav[27] << 24);
			bool isLittleEndian = wav[3] == 'F';

			consoleWrite($"isLittleEndian: {isLittleEndian}");
			consoleWrite($"bytesInFile: {wav.Length}");
			consoleWrite($"channels: {channels}");
			consoleWrite($"bytesPerSample: {bytesPerSample}");
			consoleWrite($"sampleRate: {sampleRate}");
			if (!isLittleEndian)
				quitWithError($"ERROR: file is big-endian, but we only support little endian. Header start: {Convert.ToChar(wav[0])}{Convert.ToChar(wav[1])}{Convert.ToChar(wav[2])}{Convert.ToChar(wav[3])}");
			if (bytesPerSample != 3)
				quitWithError("ERROR: bytesPerSample not supported, must be 3");

			// Get past all the other sub chunks to get to the data subchunk:
			int pos = 12;   // First Subchunk ID from 12 to 16

			// Keep iterating until we find the data chunk (i.e. 64 61 74 61 ...... (i.e. 100 97 116 97 in decimal))
			while (!(wav[pos] == 100 && wav[pos + 1] == 97 && wav[pos + 2] == 116 && wav[pos + 3] == 97))
			{
				pos += 4;
				int chunkSize = wav[pos] + wav[pos + 1] * 256 + wav[pos + 2] * 65536 + wav[pos + 3] * 16777216;
				pos += 4 + chunkSize;
			}
			pos += 8;

			// Pos is now positioned to start of actual sound data.
			headerSize = pos;
			int samples = (wav.Length - pos) / bytesPerSample;
			if (channels == 2)
				samples /= 2;

			// Allocate memory (right will be null if only mono sound)
			left = new int[samples];
			if (channels == 2)
				right = new int[samples];
			else
				right = null;

			// Write to double array(s)
			int i = 0;
			while (pos + channels * bytesPerSample <= wav.Length)
			{
				left[i] = readSample(wav, pos, bytesPerSample);
				// consoleWrite($"QQQ {left[i]}");
				pos += bytesPerSample;
				if (channels == 2)
				{
					right[i] = readSample(wav, pos, bytesPerSample);
					pos += bytesPerSample;
				}
				i++;
			}
		}


		static void cutAndSave(ref byte[] wav, int[] left, int[] right, int sampleRate, int headerSize, int bytesPerSample, string outputFolder)
		{
			// When searching for silence we ignore everything beyond a certain volume.
			// Numbers are larger when bytesPerSample is larger, so compensate for this.
			int audibleThreshold = (int)Math.Pow(20, bytesPerSample);

			float maxStandardNoteDuration = 8;
			float fadeOutDuration = 0.001f;
			float timeBetweenNotes = 8;
			int samplesBetweenNotes = (int)Math.Round(timeBetweenNotes * sampleRate);
			int numChannels = right == null ? 1 : 2;

			// Process all the notes
			int currentNoteStart = 0;
			int noteCounter = 0;
			int notesExported = 0;
			while (currentNoteStart + samplesBetweenNotes <= left.Length)
			{
				int samplesToCopy = 0;
				int fadeOutStart = 0;

				// Search backwards through the note to figure out where the audible part ends (most samples are way shorter that the maximum allowed duration)
				for (int i = (int)Math.Round(maxStandardNoteDuration * sampleRate); i > 0; i--)
				{
					// Check whether this sample is audible
					if (left[currentNoteStart + i] > audibleThreshold
						|| (right != null && right[currentNoteStart + i] > audibleThreshold))
					{
						// This sample is audible, so the note lasts until here
						fadeOutStart = i;
						samplesToCopy = i + (int)Math.Round(fadeOutDuration * sampleRate);
						break;
					}
				}
				
				// Skip notes that are entirely inaudible
				if (samplesToCopy > 0)
				{
					// Create file data
					byte[] output = new byte[headerSize + samplesToCopy * numChannels * bytesPerSample];

					// Copy header that we read, but alter the relevant parts
					for (int i = 0; i < headerSize; i++)
						output[i] = wav[i];
					output[22] = (byte)numChannels;
					// We're not writing the filesize to the header, but VLC, Unreal and Audacity all work fine so this is good enough as is

					// Copy note data
					for (int i = 0; i < samplesToCopy; ++i)
					{
						float volumeMultiplier = 1;
						if (i > fadeOutStart && fadeOutStart < samplesToCopy)
							volumeMultiplier = 1 - (float)(i - fadeOutStart) / (samplesToCopy - fadeOutStart);
						writeSample(left[currentNoteStart + i], ref output, bytesPerSample, headerSize + i * numChannels * bytesPerSample, volumeMultiplier);
						if (right != null)
							writeSample(right[currentNoteStart + i], ref output, bytesPerSample, headerSize + i * numChannels * bytesPerSample + bytesPerSample, volumeMultiplier);
					}

					// Write file
					int suffixInt = noteCounter + 1;
					// Worst thing on the planet.. is what it is though.
					string[] suffixArray = new[]
					{
						"A1", "A1S", "B1", 
						"C2", "C2S","D2","D2S","E2","F2","F2S", "G2", "G2S", "A2", "A2S", "B2",
						"C3", "C3S","D3","D3S","E3","F3","F3S", "G3", "G3S", "A3", "A3S", "B3",
						"C4", "C4S","D4","D4S","E4","F4","F4S", "G4", "G4S", "A4", "A4S", "B4",
						"C5", "C5S","D5","D5S","E5","F5","F5S", "G5", "G5S", "A5", "A5S", "B5",
						"C6", "C6S","D6","D6S","E6","F6","F6S", "G6", "G6S", "A6", "A6S", "B6",
						"C7", "C7S","D7","D7S","E7","F7","F7S", "G7", "G7S", "A7", "A7S", "B7",
						"C8", "C8S","D8","D8S","E8","F8","F8S", "G8", "G8S", "A8", "A8S", "B8",
						"C9", "C9S","D9","D9S","E9","F9","F9S", "G9", "G9S", "A9", "A9S", "B9"
					};
					string suffix = "_-_" + suffixInt.ToString("00") + "_" + suffixArray[noteCounter];
					File.WriteAllBytes($"{outputFolder}{_noteNamePrefix}{suffix}.wav", output);
					notesExported++;
				}

				// Progress to the next note
				currentNoteStart += samplesBetweenNotes;
				noteCounter++;
			}
			consoleWrite($"Num notes encountered: {noteCounter}");
			consoleWrite($"Num notes exported: {notesExported}");
		}


		static public void consoleWrite(string text)
		{
			// Show errors in red
			bool isErrorPrint = text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
			if (isErrorPrint)
				Console.ForegroundColor = ConsoleColor.Red;
			else
				Console.ResetColor();

			// Indent so that console is clearly structured during build process
			Console.WriteLine("  " + text);

			Console.ResetColor();
		}


		static private void quitWithError(string text)
		{
			consoleWrite(text);
			System.Environment.Exit(0);
		}
	}
}