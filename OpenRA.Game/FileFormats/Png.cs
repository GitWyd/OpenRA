#region Copyright & License Information
/*
 * Copyright 2007-2019 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace OpenRA.FileFormats
{
	public class Png
	{
		public int Width { get; set; }
		public int Height { get; set; }
		public Color[] Palette { get; set; }
		public byte[] Data { get; set; }
		public Dictionary<string, string> EmbeddedData = new Dictionary<string, string>();

		public Png(Stream s)
		{
			if (!Verify(s))
				throw new InvalidDataException("PNG Signature is bogus");

			s.Position += 8;
			var headerParsed = false;
			var isPaletted = false;
			var data = new List<byte>();

			for (;;)
			{
				var length = IPAddress.NetworkToHostOrder(s.ReadInt32());
				var type = Encoding.UTF8.GetString(s.ReadBytes(4));
				var content = s.ReadBytes(length);
				/*var crc = */s.ReadInt32();

				if (!headerParsed && type != "IHDR")
					throw new InvalidDataException("Invalid PNG file - header does not appear first.");

				using (var ms = new MemoryStream(content))
				{
					switch (type)
					{
						case "IHDR":
						{
							if (headerParsed)
								throw new InvalidDataException("Invalid PNG file - duplicate header.");
							Width = IPAddress.NetworkToHostOrder(ms.ReadInt32());
							Height = IPAddress.NetworkToHostOrder(ms.ReadInt32());

							var bitDepth = ms.ReadUInt8();
							var colorType = (PngColorType)ms.ReadByte();
							isPaletted = IsPaletted(bitDepth, colorType);

							var dataLength = Width * Height;
							if (!isPaletted)
								dataLength *= 4;

							Data = new byte[dataLength];

							var compression = ms.ReadByte();
							/*var filter = */ms.ReadByte();
							var interlace = ms.ReadByte();

							if (compression != 0)
								throw new InvalidDataException("Compression method not supported");

							if (interlace != 0)
								throw new InvalidDataException("Interlacing not supported");

							headerParsed = true;

							break;
						}

						case "PLTE":
						{
							Palette = new Color[256];
							for (var i = 0; i < length / 3; i++)
							{
								var r = ms.ReadByte(); var g = ms.ReadByte(); var b = ms.ReadByte();
								Palette[i] = Color.FromArgb(r, g, b);
							}

							break;
						}

						case "tRNS":
						{
							if (Palette == null)
								throw new InvalidDataException("Non-Palette indexed PNG are not supported.");

							for (var i = 0; i < length; i++)
								Palette[i] = Color.FromArgb(ms.ReadByte(), Palette[i]);

							break;
						}

						case "IDAT":
						{
							data.AddRange(content);

							break;
						}

						case "tEXt":
						{
							var key = ms.ReadASCIIZ();
							EmbeddedData.Add(key, ms.ReadASCII(length - key.Length - 1));

							break;
						}

						case "IEND":
						{
							using (var ns = new MemoryStream(data.ToArray()))
							{
								using (var ds = new InflaterInputStream(ns))
								{
									var pxStride = isPaletted ? 1 : 4;
									var stride = Width * pxStride;

									var prevLine = new byte[stride];
									for (var y = 0; y < Height; y++)
									{
										var filter = (PngFilter)ds.ReadByte();
										var line = ds.ReadBytes(stride);

										for (var i = 0; i < stride; i++)
											line[i] = i < pxStride
												? UnapplyFilter(filter, line[i], 0, prevLine[i], 0)
												: UnapplyFilter(filter, line[i], line[i - pxStride], prevLine[i], prevLine[i - pxStride]);

										Array.Copy(line, 0, Data, y * stride, line.Length);
										prevLine = line;
									}
								}
							}

							if (isPaletted && Palette == null)
								throw new InvalidDataException("Non-Palette indexed PNG are not supported.");

							return;
						}
					}
				}
			}
		}

		public static bool Verify(Stream s)
		{
			var pos = s.Position;
			var signature = new[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
			var isPng = signature.Aggregate(true, (current, t) => current && s.ReadUInt8() == t);
			s.Position = pos;
			return isPng;
		}

		static byte UnapplyFilter(PngFilter f, byte x, byte a, byte b, byte c)
		{
			switch (f)
			{
				case PngFilter.None: return x;
				case PngFilter.Sub: return (byte)(x + a);
				case PngFilter.Up: return (byte)(x + b);
				case PngFilter.Average: return (byte)(x + (a + b) / 2);
				case PngFilter.Paeth: return (byte)(x + Paeth(a, b, c));
				default:
					throw new InvalidOperationException("Unsupported Filter");
			}
		}

		static byte Paeth(byte a, byte b, byte c)
		{
			var p = a + b - c;
			var pa = Math.Abs(p - a);
			var pb = Math.Abs(p - b);
			var pc = Math.Abs(p - c);

			return (pa <= pb && pa <= pc) ? a :
				(pb <= pc) ? b : c;
		}

		[Flags]
		enum PngColorType { Indexed = 1, Color = 2, Alpha = 4 }
		enum PngFilter { None, Sub, Up, Average, Paeth }

		static bool IsPaletted(byte bitDepth, PngColorType colorType)
		{
			if (bitDepth == 8 && colorType == (PngColorType.Indexed | PngColorType.Color))
				return true;

			if (bitDepth == 8 && colorType == (PngColorType.Color | PngColorType.Alpha))
				return false;

			throw new InvalidDataException("Unknown pixel format");
		}
	}
}
