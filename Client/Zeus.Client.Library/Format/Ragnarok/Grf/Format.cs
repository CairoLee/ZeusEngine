﻿// @TODO: Use Ragnarok.FileFormat

// Uncomment or define this to disable all events and speed up file reading
//#define DISABLE_GRF_EVENTS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zeus.Client.Library.Format.Compression;

namespace Zeus.Client.Library.Format.Ragnarok.Grf {

	public class Format : IDisposable {
		public static uint GrfHeaderLen = 46;
		public static uint GrfFileLen = 17;
		public static uint GrfDefaultVersion = 0x200;
		public static uint GrfMaxFilenameLength = 0x100;
		public static List<string> SpecialExtensions = new List<string>(
				new[]{
				".gnd",
				".gat",
				".act",
				".str"
			}
		);

		private uint _filecountNumber1;
		private uint _filecountNumber2;

		private readonly List<string> _stringlist = new List<string>();
		private ulong _fileTableLength;
		private ulong _fileDataLength;
		private byte[] _filetableUncompressed = new byte[0];

		private string _filepath;
		private Stream _fileStream;
		private BinaryWriter _writer;

		private int _currentItemPercent;

		public FileItem this[string index] {
			get { return Files[index]; }
		}

		public Dictionary<string, FileItem> Files {
			get;
			protected set;
		}

		public string Filepath {
			get { return _filepath; }
			set {
				Flush();
				_filepath = value;
			}
		}


		public char[] MagicHeader {
			get;
			protected set;
		}

		public char[] AllowEncrypt {
			get;
			protected set;
		}

		public uint FileTableOffset {
			get;
			protected set;
		}

		public uint Version {
			get;
			protected set;
		}

		public byte[] FiletableUncompressed {
			get;
			protected set;
		}

		/// <summary>
		/// Gets the wasted space of the grf
		/// </summary>
		public int WastedSpace {
			get;
			private set;
		}

		/// <summary>
		/// Should the grf be repacked on saving?
		/// </summary>
		public bool AlwaysRepack {
			get;
			set;
		}

		public int WriteBytesPerTurn {
			get;
			set;
		}


#if !DISABLE_GRF_EVENTS
		public event RoGrfFileItemBeforeAddHandler BeforeItemAdd;
		public event RoGrfFileItemAddedHandler ItemAdded;
		public event RoGrfFileItemBeforeWriteHandler BeforeItemsWrite;
		public event RoGrfFileItemWriteHandler ItemWrite;
		public event RoGrfProgressUpdateHandler ProgressUpdate;
#endif


		public Format() {
			Files = new Dictionary<string, FileItem>();
			_stringlist = new List<string>();

			// Default repack setting
			AlwaysRepack = false;
			// Bytes to be written each turn (Default 10MB)
			WriteBytesPerTurn = 10485760;
		}

		public Format(string name)
			: this() {
			ReadGrf(name);
		}

		~Format() {
			Dispose();
		}


		public void Dispose() {
			Flush();
			_filetableUncompressed = new byte[0];

			if (Files != null && Files.Count > 0) {
				foreach (var item in Files.Values) {
					item.Flush(); // free Cache
				}
				Files.Clear();
			}

			_stringlist.Clear();
		}


		public void CreateNew(string filepath) {
			Dispose();

			Filepath = filepath;
			Version = GrfDefaultVersion;
			MagicHeader = "Master of Magic".ToCharArray();
			AllowEncrypt = new[] { 
				'(', 
				'c', 
				')', 
				' ', 
				'b', 
				'y', 
				' ', 
				'G', 
				'o', 
				'd', 
				'L', 
				'e', 
				's', 
				'Z', 
				'\0'
			};
			_filecountNumber1 = 0;
			_filecountNumber2 = 0;
		}


		#region GRF Read & Write
		/// <summary>
		/// Reads the full grf from the given filepath.
		/// <para>Supported versions:
		/// - 0x100
		/// - 0x101
		/// - 0x102
		/// - 0x103
		/// - 0x200</para>
		/// </summary>
		/// <param name="filepath"></param>
		/// <returns></returns>
		public bool ReadGrf(string filepath) {
			return ReadGrf(filepath, false);
		}

		/// <summary>
		/// Reads the grf from the given Filename
		/// <para>Supported versions:
		/// - 0x100
		/// - 0x101
		/// - 0x102
		/// - 0x103
		/// - 0x200</para>
		/// </summary>
		/// <param name="filepath"></param>
		/// <param name="skipFileReading">Should file reading skipped?</param>
		/// <returns></returns>
		public bool ReadGrf(string filepath, bool skipFileReading) {
			if (File.Exists(filepath) == false) {
				throw new ArgumentException("File \"" + filepath + "\" not found!", "filepath");
			}

			_filepath = filepath;
			// Reset wasted space flag
			WastedSpace = 0;

			using (_fileStream = File.OpenRead(_filepath)) {
				using (var binReader = new BinaryReader(_fileStream)) {

					MagicHeader = binReader.ReadChars(15);
					AllowEncrypt = binReader.ReadChars(15);
					FileTableOffset = binReader.ReadUInt32();
					_filecountNumber1 = binReader.ReadUInt32();
					_filecountNumber2 = binReader.ReadUInt32();
					var fileCount = (int)(_filecountNumber2 - _filecountNumber1 - 7);
					Version = binReader.ReadUInt32();

					_fileStream.Position += FileTableOffset;

					_fileDataLength = 0;
					if (Version >= 0x100 && Version <= 0x103) {
						// TODO: the 0x100 - 0x103 is taken from neoncube, please confirm this
						if (ReadFilesVersion1(binReader, fileCount, skipFileReading) == false) {
							throw new Exception("Error while reading GRF.");
						}
					} else if (Version >= 0x200) {
						if (ReadFilesVersion2(binReader, fileCount, skipFileReading) == false) {
							throw new Exception("Error while reading GRF.");
						}
					} else {
						throw new Exception("GRF Version (0x" + Version.ToString("X2") + ") not supported.");
					}
				}
			}

			// Calculate wasted space
			if (_fileDataLength > 0) {
				// Wasted space = space - calculated file length
				WastedSpace = (int)(FileTableOffset - _fileDataLength);
			}

			return true;
		}

		/// <summary>
		/// Reads the uncompressed body of versions between 0x100 and 0x103.
		/// No compression of the body but a mess on filenames.
		/// </summary>
		/// <param name="binReader"></param>
		/// <param name="fileCount"></param>
		/// <param name="skipFiles"></param>
		/// <returns></returns>
		private bool ReadFilesVersion1(BinaryReader binReader, int fileCount, bool skipFiles) {
			_fileTableLength = (ulong)(binReader.BaseStream.Length - binReader.BaseStream.Position);
			_filetableUncompressed = binReader.ReadBytes((int)_fileTableLength);

			// Read only body?
			if (skipFiles == false) {
				for (int i = 0, offset = 0; i < fileCount; i++) {
                    var itemTableOffset = (uint)offset;
                    var entryType = _filetableUncompressed[offset + 12];
                    var offset2 = offset + BitConverter.ToInt32(_filetableUncompressed, offset) + 4;

                    if (entryType == 0) {
                        offset = offset2 + 17;
                        continue;
                    }

                    var nameLen = _filetableUncompressed[offset] - 6;

					// These are client limits
					if (nameLen >= GrfMaxFilenameLength) {
						throw new Exception("Filename on index " + i + " is " + nameLen + " bytes long, max length is " + GrfMaxFilenameLength + ".");
					}

                    var nameBuf = new byte[nameLen];
                    Buffer.BlockCopy(_filetableUncompressed, offset + 6, nameBuf, 0, nameLen);

                    var name = EncryptionHelper.DecodeFileName(nameBuf);

                    // Check and fix the filename
                    if (name.Contains('\0')) {
                        name = name.Substring(0, name.IndexOf('\0'));
                    }

                    var compressedLenAligned = (uint)(BitConverter.ToInt32(_filetableUncompressed, offset2 + 4) - 37579);
                    var realLen = (uint)BitConverter.ToInt32(_filetableUncompressed, offset2 + 8);
                    var pos = (uint)BitConverter.ToInt32(_filetableUncompressed, offset2 + 13);

                    var cycle = 0;
                    var compressedLen = 0;

                    if (name.Contains(".")) {
                        var ext = "." + name.Split('.').Last().ToLower();
                        compressedLen = BitConverter.ToInt32(_filetableUncompressed, offset2) - BitConverter.ToInt32(_filetableUncompressed, offset2 + 8) - 715;

                        if (ext != ".gnd" && ext != ".gat" && ext != ".act" && ext != ".str") {
                            cycle = 1;
                            for (int j = 10; compressedLen >= j; j *= 10)
                                cycle++;
                        }
                    }

					name = Tools.UnifyPath(name);
					var item = new FileItem {
						TableOffset = itemTableOffset,
						Index = Files.Count,
						Filepath = name,
                        LengthCompressed = (uint)compressedLen,
                        LengthCompressedAlign = compressedLenAligned,
                        LengthUnCompressed = realLen,
                        Flags = entryType,
						// base offset + header length
                        DataOffset = pos + GrfHeaderLen
					};

					Files.Add(item.NameHash, item);
					_stringlist.Add(item.NameHash);

					_fileDataLength += item.LengthCompressedAlign;

					offset += (int)GrfFileLen;
#if !DISABLE_GRF_EVENTS
					OnItemAdded(item, i, fileCount);
#endif
				}
			}

			return true;
		}

		/// <summary>
		/// Reads the uncompressed body of versions equal or above 0x200.
		/// The body is ZIP (deflate) compressed.
		/// </summary>
		/// <param name="binReader"></param>
		/// <param name="fileCount"></param>
		/// <param name="skipFiles"></param>
		/// <returns></returns>
		private bool ReadFilesVersion2(BinaryReader binReader, int fileCount, bool skipFiles) {
			int lengthCompressed = binReader.ReadInt32();
			int lengthUnCompressed = binReader.ReadInt32();
			_fileTableLength = (ulong)lengthUnCompressed;
			var bufCompressed = new byte[lengthCompressed];
			_filetableUncompressed = new byte[(int)_fileTableLength];
			binReader.Read(bufCompressed, 0, lengthCompressed);

			_filetableUncompressed = Deflate.Decompress(bufCompressed);
			/*
			if (_filetableUncompressed.Length != (int)_fileTableLength) {
				throw new Exception("Filesize missmatch! Uncompressed Body Size is not equal to Uncompressed Length!");
			}
			*/
			// Only read body?
			if (skipFiles == false) {
				for (int i = 0, offset = 0; i < fileCount; i++) {
					var filepath = string.Empty;
					char c;
					var itemTableOffset = (uint)offset;

					while ((c = (char)_filetableUncompressed[offset++]) != '\0') {
						filepath += c;
					}

					filepath = Tools.UnifyPath(filepath);
					var item = new FileItem {
						TableOffset = itemTableOffset,
						Index = Files.Count,
						Filepath = filepath,
						Flags = _filetableUncompressed[offset + 12]
					};

					// File or directory?
					if (item.IsFile) {
						item.LengthCompressed = BitConverter.ToUInt32(_filetableUncompressed, offset);
						item.LengthCompressedAlign = BitConverter.ToUInt32(_filetableUncompressed, offset + 4);
						item.LengthUnCompressed = BitConverter.ToUInt32(_filetableUncompressed, offset + 8);
						// Offset is base offset + grf header
						item.DataOffset = BitConverter.ToUInt32(_filetableUncompressed, offset + 13) + GrfHeaderLen;

						// from eAtehna, DES encryption
						item.Cycle = 1;
						switch (item.Flags) {
							case 3:
								for (var lop = 10; item.LengthCompressed >= lop; lop = lop * 10, item.Cycle++) {
								}
								break;
							case 5:
								item.Cycle = 0;
								break;
							default:
								item.Cycle = -1;
								break;
						}
					} else {
						// skip dirs
						offset += (int)GrfFileLen;
						continue;
					}

					// FIX: Some files in a tested grf are duplicated? 
					//		I cant remember grf version or something else..
					if (GetFileByHash(item.NameHash) != null) {
						// Duplicate file, just skip it
						offset += (int)GrfFileLen;
						continue;
					}
					Files.Add(item.NameHash, item);
					_stringlist.Add(item.NameHash);

					_fileDataLength += item.LengthCompressedAlign;

					offset += (int)GrfFileLen;

#if !DISABLE_GRF_EVENTS
					OnItemAdded(item, i, fileCount);
#endif
				}
			}

			return true;
		}

		/// <summary>
		/// Writes the grf to the given Filename
		/// </summary>
		/// <param name="destinationPath"></param>
		/// <param name="repack"> </param>
		/// <returns></returns>
		public bool WriteGrf(string destinationPath, bool repack) {

			// Write to temp file 
			string tmpDestinationPath = destinationPath + "tmp";
			using (FileStream fs = File.OpenWrite(tmpDestinationPath)) {
				using (_writer = new BinaryWriter(fs)) {
					int iLengthUnCompressed;
					byte[] fileTableDataCompressed;

					using (var fileTableStream = new MemoryStream()) {

						_writer.Seek((int)GrfHeaderLen, SeekOrigin.Begin);

						// Write file binary data & temporary write file table
						int filesWritten;
						if (repack || AlwaysRepack) {
							filesWritten = WriteFileData(fileTableStream);
						} else {
							filesWritten = WriteFileDataDirty(fileTableStream);
						}

						// Save the offset after writing binary data
						var thisPos = (int)_writer.BaseStream.Position;
						// Write grf header
						_writer.Seek(0, SeekOrigin.Begin);
						foreach (var c in MagicHeader) {
							_writer.Write((byte)c); // header (15)
						}
						foreach (var c in AllowEncrypt) {
							_writer.Write((byte)c); // encrypt (15)
						}
						_writer.Write((uint)(thisPos - GrfHeaderLen)); // tableOffset
						_writer.Write(_filecountNumber1);
						_writer.Write((uint)(filesWritten + _filecountNumber1 + 7)); // number2
						// Always default version
						Version = GrfDefaultVersion;
						_writer.Write(Version); // GRF Version
						_writer.Seek(thisPos, SeekOrigin.Begin);

						// Compress file table data
						iLengthUnCompressed = (int)fileTableStream.Length;
						fileTableDataCompressed = Deflate.Compress(fileTableStream.ToArray(), true);
					}

					// Write length and data
					_writer.Write(fileTableDataCompressed.Length); // compressed
					_writer.Write(iLengthUnCompressed); // uncompressed
					_writer.Write(fileTableDataCompressed, 0, fileTableDataCompressed.Length); // data itself
				}

			}

			// If we want to overwrite the previous opened GRF, close it first
			if (_filepath == destinationPath) {
				Flush();
			}

			// Ensure nothing blocks the move
			File.Delete(destinationPath);
			// Move it finally
			File.Move(destinationPath + "tmp", destinationPath);

			// Fore clean up
			GC.Collect();

			return true;
		}
		#endregion


		#region File Operations
		/// <summary>
		/// Returns the grf file from the given index
		/// <para>Note: return null if not found</para>
		/// </summary>
		/// <param name="index"></param>
		/// <returns></returns>
		public FileItem GetFileByIndex(int index) {
			if (index >= 0 && index < _stringlist.Count) {
				return GetFileByHash(_stringlist[index]);
			}

			return null;
		}
		/// <summary>
		/// Builds the hash from the given filepath and returns the grf file with the hash
		/// <para>Note: returns null if not found</para>
		/// </summary>
		/// <param name="filepath">File filepath</param>
		/// <returns></returns>
		public FileItem GetFileByName(string filepath) {
			var nameHash = FileItem.BuildNameHash(filepath);
			return GetFileByHash(nameHash);
		}
		/// <summary>
		/// Returns the grf file with the given name hash
		/// <para>Note: returns null if not found</para>
		/// </summary>
		/// <param name="nameHash">File name hash</param>
		/// <returns></returns>
		public FileItem GetFileByHash(string nameHash) {
			if (Files.ContainsKey(nameHash)) {
				return Files[nameHash];
			}

			return null;
		}

		/// <summary>
		/// Starts a hard strings search through the intenal list, begining on index 0,
		/// and returns the file name hash, if found
		/// <para>Note: returns null, if not found</para>
		/// </summary>
		/// <param name="filename">Part of the filename or path to search</param>
		/// <returns></returns>
		public string ContainsFilepart(string filename) {
			return ContainsFilepart(filename, 0);
		}
		/// <summary>
		/// Starts a hard strings search through the intenal list and returns the file name hash, if found
		/// <para>Note: returns null, if not found</para>
		/// </summary>
		/// <param name="filename">Part of the filename or path to search</param>
		/// <param name="startIndex">The index to start the search</param>
		/// <returns></returns>
		public string ContainsFilepart(string filename, int startIndex) {
			if (startIndex + 1 > _stringlist.Count) {
				return null;
			}

			// need a hard Search =|
			filename = Tools.UnifyPath(filename);
			for (int index = startIndex; index < _stringlist.Count; index++) {
				string key = _stringlist[index];
				if (Files[key].Filepath.Contains(filename)) {
					return key;
				}
			}
			return null;
		}

        /// <summary>
        /// Starts a hard strings search through the intenal list and returns the file name hash, if found
        /// <para>Note: returns null, if not found</para>
        /// </summary>
        /// <param name="filename">Part of the filename or path to search</param>
        /// <returns></returns>
        public FileItem SearchByLinq(string filename) {
            // Try to get direct
            var uniFilename = Tools.UnifyPath(filename);
            var entry = GetFileByName(uniFilename);
            if (entry != null) {
                return entry;
            }
            
            // Hard search
            return Files.Values.FirstOrDefault(item => item.Filepath.ToLower().Contains(uniFilename));
        }

		/// <summary>
		/// Add the grf file to the internal lists
		/// <para>Note: File will be deleted and a clone added if the file already exists</para>
		/// </summary>
		/// <param name="item">The grf file</param>
		/// <returns>The new item state</returns>
		public FileItemState AddFile(FileItem item) {
			FileItem existingItem;
			bool replaceExistingItem = false;
			if ((existingItem = GetFileByHash(item.NameHash)) != null) {
				// Replace old item
				if (existingItem.IsAdded) {
					// A newly added item should be replaced..
					// Remove it and add as new one
					DeleteFile(existingItem);
					replaceExistingItem = true;
				} else {
					// Update existing item with new uncompressed data
					existingItem.State |= FileItemState.Updated;
					// Mark as not deleted
					existingItem.State &= ~FileItemState.Deleted;

					// Check for file data
					if (item.IsAdded && item.NewFilepath != null && File.Exists(item.NewFilepath)) {
						existingItem.NewFilepath = item.NewFilepath;
					} else if (item.FileData != null) {
						// Save data in tmp file
						string tmpFilepath = Path.GetTempPath();
						File.WriteAllBytes(tmpFilepath, item.FileData);
						existingItem.NewFilepath = tmpFilepath;
					} else {
						throw new Exception("Unable to fetch item data.");
					}

					// Updated compressed length
					existingItem.LengthCompressed = (uint)new FileInfo(existingItem.NewFilepath).Length;
					existingItem.FileData = new byte[0];
					// Inform the client about the update of an existing item
					return FileItemState.Updated;
				}
			}

			var newItem = item.Clone() as FileItem;
			if (newItem == null) {
				throw new Exception("Failed to clone item.");
			}

			// Realy new item or just a replace?
			if (replaceExistingItem) {
				// Just replace the reference
				newItem.State = FileItemState.Updated;
				Files[newItem.NameHash] = newItem;
			} else {
				// Add new item
				newItem.State = FileItemState.Added;
				Files.Add(newItem.NameHash, newItem);
				_stringlist.Add(newItem.NameHash);
			}

			// Okay, this is handy..
			// we return true, because the file was added as new item
			// buuut.. if the file was previously found AND the existing one was a NEW file
			// the new file will be deleted and the new one added
			//
			// so we need to "fake" the result, because of we dont add a new one
			// we replace the existing one..
			// complicated..
			if (replaceExistingItem) {
				// We didnt add a new item
				return FileItemState.Updated;
			}

			return FileItemState.Added;
		}

		/// <summary>
		/// Removes the given file from internal lists
		/// The grf needs to be saved to save the changes
		/// </summary>
		/// <param name="item">The grf file</param>
		public void DeleteFile(FileItem item) {
			DeleteFile(FileItem.BuildNameHash(item.Filepath));
		}
		/// <summary>
		/// Removes the given file from internal lists
		/// The grf needs to be saved to save the changes
		/// </summary>
		/// <param name="nameHash"></param>
		public void DeleteFile(string nameHash) {
			if (Files.ContainsKey(nameHash) == false) {
				throw new ArgumentException("Index (" + nameHash + ") does not exist", "nameHash");
			}

			// Mark es deleted
			Files[nameHash].State |= FileItemState.Deleted;
			//Files.Remove(nameHash);
			//_stringlist.Remove(nameHash);
		}


		/// <summary>
		/// Extract the file data to the given path
		/// <para>Note: the full path will be RootFolder + FilePath</para>
		/// </summary>
		/// <param name="rootFolder">The lokal root to save the data</param>
		/// <param name="index">File index from mStringList</param>
		/// <param name="clearData">Should the data be cleaned (item.Flush()) after writing?</param>
		public void ExtractFile(string rootFolder, int index, bool clearData) {
			ExtractFile(rootFolder, index, clearData, false);
		}

		/// <summary>
		/// Extract the file data to the given path
		/// <para>Note: The FilePath will be ignored if ignoreFilePath is set to true</para>
		/// </summary>
		/// <param name="rootFolder">The lokal root to save the data</param>
		/// <param name="index">File index from mStringList</param>
		/// <param name="clearData">Should the data be cleaned (item.Flush()) after writing?</param>
		/// <param name="ignoreFilePath">Ignore item filepath?</param>
		public void ExtractFile(string rootFolder, int index, bool clearData, bool ignoreFilePath) {
			if (index >= 0 && index < _stringlist.Count) {
				ExtractFile(rootFolder, _stringlist[index], clearData, ignoreFilePath);
			}
			throw new ArgumentException("Index (" + index + ") is out of range", "index");
		}

		/// <summary>
		/// Extract the file data to the given path
		/// <para>Note: the full path will be RootFolder + FilePath</para>
		/// </summary>
		/// <param name="rootFolder">The lokal root to save the data</param>
		/// <param name="nameHash">File name hash</param>
		/// <param name="clearData">Should the data be cleaned (item.Flush()) after writing?</param>
		public void ExtractFile(string rootFolder, string nameHash, bool clearData) {
			ExtractFile(rootFolder, nameHash, clearData, false);
		}

		/// <summary>
		/// Extract the file data to the given path
		/// <para>Note: The FilePath will be ignored if ignoreFilePath is set to true</para>
		/// </summary>
		/// <param name="rootFolder">The lokal root to save the data</param>
		/// <param name="nameHash">File name hash</param>
		/// <param name="clearData">Should the data be cleaned (item.Flush()) after writing?</param>
		/// <param name="ignoreFilePath">Ignore item filepath?</param>
		public void ExtractFile(string rootFolder, string nameHash, bool clearData, bool ignoreFilePath) {
			if (Files.ContainsKey(nameHash) == false) {
				throw new ArgumentException("Index (" + nameHash + ") does not exist", "nameHash");
			}

			ExtractFile(rootFolder, Files[nameHash], clearData, ignoreFilePath);
		}

		/// <summary>
		/// Extract the file data to the given path
		/// <para>Note: the full path will be RootFolder + FilePath</para>
		/// </summary>
		/// <param name="rootFolder">The lokal root to save the data</param>
		/// <param name="item">The grf file</param>
		/// <param name="clearData">Should the data be cleaned (item.Flush()) after writing?</param>
		public void ExtractFile(string rootFolder, FileItem item, bool clearData) {
			ExtractFile(rootFolder, item, clearData, false);
		}

		/// <summary>
		/// Extract the file data to the given path
		/// <para>Note: The FilePath will be ignored if ignoreFilePath is set to true</para>
		/// </summary>
		/// <param name="rootFolder">The lokal root to save the data</param>
		/// <param name="item">The grf file</param>
		/// <param name="clearData">Should the data be cleaned (item.Flush()) after writing?</param>
		/// <param name="ignoreFilePath">Ignore item filepath?</param>
		public void ExtractFile(string rootFolder, FileItem item, bool clearData, bool ignoreFilePath) {
			if (item.Flags == 0 || item.IsAdded) {
				return;
			}

			byte[] data = GetFileData(item, true);
			rootFolder = Tools.UnifyPath(rootFolder);
			if (rootFolder.EndsWith("/") == false) {
				rootFolder += "/";
			}

			string extractDir = rootFolder;
			if (ignoreFilePath == false) {
				extractDir = Path.Combine(extractDir, Tools.UnifyPath(Path.GetDirectoryName(item.Filepath)));
			}
			if (Directory.Exists(extractDir) == false) {
				Directory.CreateDirectory(extractDir);
			}

			var filename = Path.GetFileName(item.Filepath);
			if (string.IsNullOrEmpty(filename)) {
				throw new Exception("Unable to extract filename from item filepath: " + item.Filepath);
			}
			var extractFilepath = Path.Combine(extractDir, filename);
			try {
				if (File.Exists(extractFilepath)) {
					File.Delete(extractFilepath);
				}
			} catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

			File.WriteAllBytes(extractFilepath, data);

			if (clearData) {
				item.FileData = null;
			}
		}


		#region Filedata Handler
		/// <summary>
		/// Returns the file data, decompressed if needed
		/// </summary>
		/// <param name="index">File index in mStringList</param>
		/// <param name="decompress">Should the data decompressed?</param>
		/// <returns></returns>
		public byte[] GetFileData(int index, bool decompress) {
			if (index >= 0 && index < _stringlist.Count) {
				return GetFileData(_stringlist[index], decompress);
			}
			throw new ArgumentException("Index (" + index + ") is out of range", "index");
		}
		/// <summary>
		/// Returns the file data, decompressed if needed
		/// </summary>
		/// <param name="nameHash">File name hash</param>
		/// <param name="decompress">Should the data decompressed?</param>
		/// <returns></returns>
		public byte[] GetFileData(string nameHash, bool decompress) {
			if (Files.ContainsKey(nameHash) == false) {
				throw new ArgumentException("Index (" + nameHash + ") does not exist", "nameHash");
			}

			return GetFileData(Files[nameHash], decompress);
		}
		/// <summary>
		/// Returns the file data, decompressed if needed
		/// </summary>
		/// <param name="item">The grf file</param>
		/// <param name="decompress">Should the data decompressed?</param>
		/// <returns></returns>
		public byte[] GetFileData(FileItem item, bool decompress) {
			byte[] buf = null;
			bool isUpdated = item.IsAdded || item.IsUpdated;
			if (isUpdated) {
				// Load data from file
				buf = File.ReadAllBytes(item.NewFilepath);
			} else if (item.FileData == null || item.FileData.Length != item.LengthCompressedAlign) {
				// Cache data
				CacheFileData(item);
				buf = item.FileData;
            } else {
                buf = item.FileData;
            }

			if (isUpdated == false && buf != null && buf.Length > 0) {
				// deocde, if needed
				if (item.Cycle >= 0 && Deflate.IsMagicHead(buf) == false) {
					EncryptionHelper.DecryptFileData(buf, item.Cycle == 0, item.Cycle);
				}

				// Decompress data
				if (decompress) {
					buf = Deflate.Decompress(buf);
				}
			}

			return buf;
		}

		/// <summary>
		/// Caches the file data 
		/// </summary>
		/// <param name="nameHash">File name hash</param>
		public void CacheFileData(string nameHash) {
			if (Files.ContainsKey(nameHash) == false) {
				throw new ArgumentException("Index (" + nameHash + ") not found", "nameHash");
			}

			CacheFileData(Files[nameHash]);
		}
		/// <summary>
		/// Caches the file data
		/// <para>Note: Updated files wont be recached!</para>
		/// <para>Only an empty Buffer will be created, if data is null</para>
		/// </summary>
		/// <param name="item">The grf file</param>
		public void CacheFileData(FileItem item) {
			EnsureFilestream();

			// Data from added or updated files
			if (item.IsAdded || item.IsUpdated) {
				item.FileData = File.ReadAllBytes(item.NewFilepath);
				return;
			}
			item.FileData = new byte[item.LengthCompressedAlign];
			if (item.LengthCompressedAlign > 0) { // maybe its a Directory
				_fileStream.Seek(item.DataOffset, SeekOrigin.Begin);
				if ((_fileStream.Position + item.LengthCompressedAlign) >= _fileStream.Length) {
					throw new Exception("End of Stream reached - can not read Filedata from GRF!");
				}
				_fileStream.Read(item.FileData, 0, (int)item.LengthCompressedAlign);
			}
		}
		#endregion
		#endregion


		#region Save & Clean
		/// <summary>
		/// Writes the whole grf to the origin file
		/// </summary>
		public void Save() {
			Save(Filepath);
		}

		/// <summary>
		/// Writes the whole grf to the origin file using the given repack option.
		/// </summary>
		/// <param name="repack"></param>
		public void Save(bool repack) {
			Save(Filepath, repack);
		}

		/// <summary>
		/// Writes the whole grf to the given filename
		/// </summary>
		/// <param name="destinationPath"></param>
		public void Save(string destinationPath) {
			Save(destinationPath, AlwaysRepack);
		}

		/// <summary>
		/// Writes the whole grf to the given filename using the given repack option.
		/// </summary>
		/// <param name="destinationPath"></param>
		/// <param name="repack"></param>
		public void Save(string destinationPath, bool repack) {
			Flush();

			WriteGrf(destinationPath, repack);
		}

		/// <summary>
		/// Flushs (close) the filestreams
		/// </summary>
		public void Flush() {
			if (_fileStream == null) {
				return;
			}
			try {
				_fileStream.Close();
				_fileStream.Dispose();
			} catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
		}
		#endregion


		#region GRF Events
#if !DISABLE_GRF_EVENTS
		/// <summary>
		/// Triggers the BeforeItemAdd event.
		/// </summary>
		public void OnBeforeItemAdd() {
			_currentItemPercent = 0;

			if (BeforeItemAdd != null) {
				BeforeItemAdd();
			}
		}

		/// <summary>
		/// Triggers the ItemAdded event.
		/// </summary>
		/// <param name="item"></param>
		/// <param name="num"></param>
		/// <param name="maxCount"></param>
		public void OnItemAdded(FileItem item, int num, int maxCount) {
			if (ItemAdded == null) {
				return;
			}

			var p = (int)(((float)num / maxCount) * 100);
			if (p == _currentItemPercent) {
				return;
			}

			_currentItemPercent = p;
			ItemAdded(item, p);
		}

		/// <summary>
		/// Triggers the BeforeItemWrite event.
		/// </summary>
		public void OnBeforeItemsWrite() {
			_currentItemPercent = 0;

			if (BeforeItemsWrite != null) {
				BeforeItemsWrite();
			}
		}

		/// <summary>
		/// Triggers the ItemWrite event.
		/// </summary>
		/// <param name="item"></param>
		public void OnItemWrite(FileItem item) {
			if (ItemWrite == null) {
				return;
			}

			var p = (int)(((float)item.Index / Files.Count) * 100);
			if (p == _currentItemPercent) {
				return;
			}

			_currentItemPercent = p;
			ItemWrite(item, p);
		}

		/// <summary>
		/// Triggers the ProgressUpdate event.
		/// </summary>
		/// <param name="progress"></param>
		public void OnProgressUpdate(int progress) {
			if (_currentItemPercent == progress) {
				return;
			}

			_currentItemPercent = progress;
			if (ProgressUpdate == null) {
				return;
			}

			ProgressUpdate(_currentItemPercent);
		}
#endif
		#endregion


		#region private Helper
		/// <summary>
		/// Ensure the stream to the origin filepath is readable.
		/// </summary>
		private void EnsureFilestream() {
			if (_fileStream == null || _fileStream.CanRead == false) {
				if (_fileStream != null) {
					try {
						_fileStream.Close();
					} catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
				}

				_fileStream = File.OpenRead(_filepath);
			}
		}

		/// <summary>
		/// Writes the properties of each item into the fileTableData stream
		/// and the binary data into main stream (<see cref="_writer"/>)
		/// </summary>
		/// <param name="fileTableStream"></param>
		private int WriteFileData(Stream fileTableStream) {
			var tableWriter = new BinaryWriter(fileTableStream);

#if !DISABLE_GRF_EVENTS
			OnBeforeItemsWrite();
#endif

			int filesWritten = 0;
			foreach (string index in _stringlist) {
				var item = this[index];
				// Dont write null values or folders
				if (item == null || item.Flags == 0 || item.IsDeleted) {
					continue;
				}

				// Write to streams
				item.WriteToStream(this, _writer, tableWriter);
				// Clean memory
				item.Flush();

				// Trigger callbacks
#if !DISABLE_GRF_EVENTS
				OnItemWrite(item);
#endif

				filesWritten++;
			}

			// Dont close stream's because that would close all underlying streams too
			return filesWritten;
		}

		/// <summary>
		/// Copies item properties and binary data directly from origin grf.
		/// Changed items will be ignored on copy and added after the last origin item.
		/// New added items will be added after last origin item or changed item, if present.
		/// </summary>
		/// <param name="fileTableStream"></param>
		private int WriteFileDataDirty(Stream fileTableStream) {
			var tableWriter = new BinaryWriter(fileTableStream);

			// Trigger event and reset internal percent state
#if !DISABLE_GRF_EVENTS
			OnBeforeItemsWrite();
#endif

			// Something to copy?
			int filesWritten = 0;
			if (_filetableUncompressed != null && _filetableUncompressed.Length > 0) {

				// Copy file binary data
				// Start: GrfHeaderLen
				// End: GrfHeaderLen + FileTableOffset
				try {
					// Ensure a readable stream
					EnsureFilestream();
					// Seek to binary data
					_fileStream.Position = GrfHeaderLen;
					// Write binary data to destination
					int bytesRead, bytesLeft = (int)FileTableOffset, bytesMax = bytesLeft;
					var bufRead = new byte[WriteBytesPerTurn]; // bytes per turn
					var bytesToRead = (bytesLeft < bufRead.Length ? bytesLeft : bufRead.Length);
#if !DISABLE_GRF_EVENTS
#endif
					while ((bytesRead = _fileStream.Read(bufRead, 0, bytesToRead)) > 0) {
						// Write the bytes
						_writer.Write(bufRead, 0, bytesRead);
						// Calculate left bytes
						bytesLeft -= bytesRead;
						// Claculate current progress in percent (0 - 100)
#if !DISABLE_GRF_EVENTS
						var percState = (int)(((bytesMax - bytesLeft) / (float)bytesMax) * 100f);
						if (percState != _currentItemPercent) {
							OnProgressUpdate(percState);
						}
#endif
						// Finished?
						if (bytesLeft <= 0) {
							break;
						}
						if (bytesLeft < bytesToRead) {
							// Change the amount of bytes to read to prevent overflow exception
							bytesToRead = bytesLeft;
						}
					}

				} catch (Exception ex) {
					System.Diagnostics.Debug.WriteLine(ex);
				}
			}

			// Write file property data (file table), and append new or updated files
			foreach (string index in _stringlist) {
				var item = this[index];
				// Dont write null values or folders
				if (item == null || item.Flags == 0) {
					continue;
				}
				// Deleted?
				if (item.IsDeleted) {
					// Skip file
					continue;
				}

				// Write new or updated binary data
				if (item.IsAdded || item.IsUpdated) {
					item.WriteToBinaryTable(this, _writer);
				}
				// Write file properties
				item.WriteToFileTable(tableWriter);
				// Clean memory
				item.Flush();

				filesWritten++;
			}

			// Dont close tableWriter because that would close the underlying stream too (which needs to be accessed later on again)
			// Just a flush on origin streams to clean up buffers
			Flush();

			// Inform client 
#if !DISABLE_GRF_EVENTS
			OnProgressUpdate(100);
#endif

			return filesWritten;
		}
		#endregion

	}

}
