﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluxionSharp.Exceptions;

namespace FluxionSharp
{
    #region Fluxion

    /// <summary>
    ///     Static class that handles read/write operations.
    /// </summary>
    public static class Fluxion
    {
        #region Version

        /// <summary>
        ///     Version of the current Fluxion on this library.
        /// </summary>
// ReSharper disable once MemberCanBePrivate.Global
        public const byte Version = 2;

        // ReSharper disable once MemberCanBePrivate.Global
        public static readonly byte[] FluxionMark = { 0x46, 0x4c, 0x58 };

        #endregion

        #region Read

        /// <summary>
        ///     Reads <paramref name="stream" /> as Fluxion nodes.
        /// </summary>
        /// <param name="stream">Stream to read.</param>
        /// <returns>A root <see cref="FluxionNode" />.</returns>
        /// <exception cref="FluxionInvalidHeaderException">
        ///     Exception thrown if the <paramref name="stream" /> does not start with
        ///     "FLX".
        /// </exception>
        /// <exception cref="FluxionEndOfStreamException">
        ///     Exception thrown if the end of the stream is reached but expected more
        ///     data.
        /// </exception>
        public static FluxionNode Read(Stream stream)
        {
            var root = new FluxionNode { IsRoot = true, Version = Version };

            var byte_F = stream.ReadByte();
            var byte_L = stream.ReadByte();
            var byte_X = stream.ReadByte();

            if (byte_F != 0x46 && byte_L != 0x4C && byte_X != 0x58)
                throw new FluxionInvalidHeaderException();

            var versionByte = stream.ReadByte();
            if (versionByte == -1)
                throw new FluxionEndOfStreamException();

            var encodingByte = stream.ReadByte();
            if (encodingByte == -1)
                throw new FluxionEndOfStreamException();

            var encoding = GetEncoding((byte)encodingByte);

            switch (versionByte)
            {
                case 1:
                    root = ReadRecurse_V1(stream, encoding, root, true);
                    break;
                case 2:
                    root = ReadRecurse_V2(stream, encoding, root, true);
                    break;

                default:
                    throw new FluxionUnsupportedVersionException(root.Version);
            }

            root.Version = (byte)versionByte;
            return root;
        }

        /// <summary>
        ///     Reads a Fluxion root node from a file.
        /// </summary>
        /// <param name="fileName">The path to the file.</param>
        /// <param name="fileShare">Determines if the file should be accessed by other processes.</param>
        /// <returns>A root <see cref="FluxionNode" />.</returns>
        /// <exception cref="FileNotFoundException">Exception thrown if file was not found.</exception>
        public static FluxionNode Read(string fileName, FileShare fileShare = FileShare.ReadWrite)
        {
            if (!File.Exists(fileName))
                throw new FileNotFoundException($"File \"{fileName}\" was not found.");
            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, fileShare))
            {
                return Read(fs);
            }
        }

        #region Fluxion 2

        private static FluxionNode ReadRecurse_V2(
            Stream stream,
            Encoding encoding,
            FluxionNode rootNode,
            bool readRoot = false
        )
        {
            if (readRoot)
            {
                var treeMarkStart = DecodeVarLong(stream);
                stream.Seek(treeMarkStart, SeekOrigin.Begin);
            }

            var node = readRoot ? rootNode : new FluxionNode { IsRoot = false, Parent = rootNode };

            var valueType = stream.ReadByte();
            if (valueType == -1)
                throw new FluxionEndOfStreamException();

            var hasName = isBitSet((byte)valueType, 4);
            valueType -= hasName ? 16 : 0;
            var noChild = isBitSet((byte)valueType, 5);
            valueType -= noChild ? 32 : 0;
            var noAttr = isBitSet((byte)valueType, 6);
            valueType -= noAttr ? 64 : 0;
            var uniqueFlag = isBitSet((byte)valueType, 7);
            valueType -= uniqueFlag ? 128 : 0;

            var childrenCount = 0;
            if (!noChild) childrenCount = DecodeVarInt(stream);

            if (hasName)
            {
                var namePos = DecodeVarLong(stream);
                var pos = stream.Position;
                stream.Seek(namePos, SeekOrigin.Begin);
                node.Name = encoding.GetString(DecodeByteArrWithVarInt(stream));
                stream.Seek(pos, SeekOrigin.Begin);
            }

            node.Value = ReadBytesFromType_V2(stream, valueType, encoding, uniqueFlag);

            if (!noAttr)
            {
                var attrCount = DecodeVarInt(stream);

                for (var i = 0; i < attrCount; i++)
                {
                    var attr = new FluxionAttribute();
                    var attr_valueType = stream.ReadByte();
                    if (attr_valueType == -1)
                        throw new FluxionEndOfStreamException();

                    var attr_hasName = isBitSet((byte)attr_valueType, 4);
                    attr_valueType -= attr_hasName ? 16 : 0;

                    var attr_uniqueFlag = isBitSet((byte)attr_valueType, 7);
                    valueType -= attr_uniqueFlag ? 128 : 0;

                    if (attr_hasName)
                    {
                        var namePos = DecodeVarLong(stream);
                        var pos = stream.Position;
                        stream.Seek(namePos, SeekOrigin.Begin);
                        attr.Name = encoding.GetString(DecodeByteArrWithVarInt(stream));
                        stream.Seek(pos, SeekOrigin.Begin);
                    }

                    attr.Value = ReadBytesFromType_V2(stream, attr_valueType, encoding, attr_uniqueFlag);

                    node.Attributes.Add(attr);
                }
            }

            if (noChild) return node;

            for (var i = 0; i < childrenCount; i++)
                node.Add(ReadRecurse_V2(stream, encoding, node));


            return node;
        }

        #endregion Fluxion 2

        #region Fluxion 1

        private static FluxionNode ReadRecurse_V1(
            Stream stream,
            Encoding encoding,
            FluxionNode rootNode,
            bool readRoot = false
        )
        {
            var node = readRoot ? rootNode : new FluxionNode { IsRoot = false, Parent = rootNode };

            var valueType = stream.ReadByte();
            if (valueType == -1)
                throw new FluxionEndOfStreamException();

            // Check flags
            var hasName = isBitSet((byte)valueType, 4);
            valueType -= hasName ? 16 : 0;
            var noChild = isBitSet((byte)valueType, 5);
            valueType -= noChild ? 32 : 0;
            var noAttr = isBitSet((byte)valueType, 6);
            valueType -= noAttr ? 64 : 0;

            // Get Child Count
            var childrenCount = 0;
            if (!noChild) childrenCount = DecodeVarInt(stream);

            if (hasName) node.Name = encoding.GetString(DecodeByteArrWithVarInt(stream));

            // Read value here
            node.Value = ReadBytesFromType(stream, valueType, encoding);

            if (!noAttr)
            {
                var attrCount = DecodeVarInt(stream);

                for (var i = 0; i < attrCount; i++)
                {
                    var attr = new FluxionAttribute();
                    var attr_valueType = stream.ReadByte();
                    if (attr_valueType == -1)
                        throw new FluxionEndOfStreamException();

                    var attr_hasName = isBitSet((byte)attr_valueType, 4);
                    attr_valueType -= attr_hasName ? 16 : 0;

                    if (attr_hasName)
                    {
                        var attr_nameBytes = DecodeByteArrWithVarInt(stream);
                        attr.Name = encoding.GetString(attr_nameBytes);
                    }

                    attr.Value = ReadBytesFromType(stream, attr_valueType, encoding);

                    node.Attributes.Add(attr);
                }
            }

            if (noChild) return node;
            {
                for (var i = 0; i < childrenCount; i++)
                    node.Add(ReadRecurse_V1(stream, encoding, node));
            }

            return node;
        }

        #endregion Fluxion 1

        #endregion Read

        #region Write

        /// <summary>
        ///     Writes a Fluxion node (turns it to Root node in the process) to a stream.
        /// </summary>
        /// <param name="node">Node to write.</param>
        /// <param name="stream">Stream to write.</param>
        /// <param name="encoding">Encodings of the string values and names.</param>
        /// <param name="version">Version of the Fluxion to use (for backwards compatibility). Use 0 for current version.</param>
        public static void Write(this FluxionNode node, Stream stream, Encoding encoding, byte version = 0)
        {
            while (true)
            {
                switch (version)
                {
                    case 0:
                        version = Version;
                        continue;

                    case 1:
                        Write_V1(node, stream, encoding, true);
                        break;

                    case 2:
                        Write_V2(node, stream, encoding, true, Array.Empty<AnalyzedDataContent>());
                        break;

                    default:
                        throw new FluxionUnsupportedVersionException(version);
                }

                break;
            }
        }

        /// <summary>
        ///     Writes a Fluxion node to a file.
        /// </summary>
        /// <param name="node">Node to write.</param>
        /// <param name="fileName">Path of the file.</param>
        /// <param name="encoding">Determines the encoding of the string values and names.</param>
        /// <param name="fileShare">Determines if other processes can access the file while writing to it.</param>
        /// <param name="version">Version of the Fluxion to use (for backwards compatibility).</param>
        // ReSharper disable once UnusedMember.Global
        public static void Write(
            this FluxionNode node,
            string fileName,
            Encoding encoding,
            FileShare fileShare = FileShare.ReadWrite,
            byte version = 0
        )
        {
            encoding = encoding ?? Encoding.Default;
            using (
                var stream = File.Exists(fileName)
                    ? new FileStream(fileName, FileMode.Truncate, FileAccess.ReadWrite, fileShare)
                    : File.Create(fileName)
            )
            {
                Write(node, stream, encoding, version);
            }
        }

        #region Fluxion 2

        private class AnalyzedDataContent
        {
            public AnalyzedDataContent(long pos, object data, bool isHash = false)
            {
                Position = pos;
                Data = data;
                IsHash = isHash;
            }

            public long Position { get; internal set; }
            public object Data { get; }
            public bool IsHash { get; }
            public byte[] HashedArray { get; internal set; }
        }

        private static long WriteData_V2(Stream stream, Encoding encoding,
            ref List<AnalyzedDataContent> adc)
        {
            if (adc is null)
                return stream.Position;

            foreach (var data in adc)
            {
                data.Position = stream.Position;
                WriteValue_V2(data.Data is byte[] ? data.HashedArray : data.Data, stream, encoding);
            }

            return stream.Position;
        }

        private static long Estimate_V2(this FluxionNode node, Encoding encoding,
            ref List<AnalyzedDataContent> adc)
        {
            long estimation = 0;
            if (adc is null)
                adc = new List<AnalyzedDataContent>();


            if (!string.IsNullOrWhiteSpace(node.Name) &&
                adc.FindAll(it => !it.IsHash && it.Data is string s && s == node.Name) is List<AnalyzedDataContent>
                    list_N1 &&
                list_N1.Count <= 0 &&
                !string.IsNullOrWhiteSpace(node.Name))
            {
                adc.Add(new AnalyzedDataContent(0, node.Name));
                estimation += EstimateValueSize_V2(node.Name, encoding);
            }

            switch (node.Value)
            {
                case null:
                case false:
                case true:
                    break;
                case byte[] byteArray:
                    if (byteArray.Length <= 0) break;
                    using (var sha256Hash = SHA256.Create())
                    {
                        var hash = sha256Hash.ComputeHash(byteArray);
                        if (adc.FindAll(it => it.IsHash && it.Data == hash) is List<AnalyzedDataContent> list_h &&
                            list_h.Count > 0)
                            break;
                        adc.Add(new AnalyzedDataContent(0, hash, true) { HashedArray = hash });
                        estimation += EstimateValueSize_V2(byteArray, encoding);
                    }

                    break;

                default:
                    if (adc.FindAll(it => it.Data == node.Value) is List<AnalyzedDataContent> list &&
                        list.Count > 0)
                        break;
                    if ((node.Value is string s && string.IsNullOrWhiteSpace(s)) ||
                        (node.Value is byte byteValue && byteValue == 0) ||
                        (node.Value is sbyte sbyteValue && sbyteValue == 0) ||
                        (node.Value is char charValue && charValue == char.MinValue) ||
                        (node.Value is short shortValue && shortValue == 0) ||
                        (node.Value is ushort ushortValue && ushortValue == 0) ||
                        (node.Value is int intValue && intValue == 0) ||
                        (node.Value is uint uintValue && uintValue == 0) ||
                        (node.Value is long longValue && longValue == 0) ||
                        (node.Value is ulong ulongValue && ulongValue == 0) ||
                        (node.Value is float floatValue && floatValue == 0) ||
                        (node.Value is double doubleValue && doubleValue == 0)) break;
                    adc.Add(new AnalyzedDataContent(0, node.Value));
                    estimation += EstimateValueSize_V2(node.Value, encoding);
                    break;
            }

            foreach (FluxionAttribute attr in node.Attributes)
            {
                if (!string.IsNullOrWhiteSpace(attr.Name) &&
                    adc.FindAll(it => !it.IsHash && it.Data is string s && s == attr.Name) is List<AnalyzedDataContent>
                        list_N &&
                    list_N.Count <= 0 &&
                    !string.IsNullOrWhiteSpace(attr.Name))
                {
                    adc.Add(new AnalyzedDataContent(0, attr.Name));
                    estimation += EstimateValueSize_V2(attr.Name, encoding);
                }

                switch (attr.Value)
                {
                    case null:
                    case false:
                    case true:
                        break;
                    case byte[] byteArray:
                        if (byteArray.Length <= 0) break;
                        using (var sha256Hash = SHA256.Create())
                        {
                            var hash = sha256Hash.ComputeHash(byteArray);
                            if (adc.FindAll(it => it.IsHash && it.Data == hash) is List<AnalyzedDataContent> list_h &&
                                list_h.Count > 0)
                                break;
                            adc.Add(new AnalyzedDataContent(0, hash, true) { HashedArray = hash });
                            estimation += EstimateValueSize_V2(byteArray, encoding);
                        }

                        break;

                    default:
                        if (adc.FindAll(it => it.Data == attr.Value) is List<AnalyzedDataContent> list &&
                            list.Count > 0)
                            break;
                        if ((attr.Value is string s && string.IsNullOrWhiteSpace(s)) ||
                            (attr.Value is byte byteValue && byteValue == 0) ||
                            (attr.Value is sbyte sbyteValue && sbyteValue == 0) ||
                            (attr.Value is char charValue && charValue == char.MinValue) ||
                            (attr.Value is short shortValue && shortValue == 0) ||
                            (attr.Value is ushort ushortValue && ushortValue == 0) ||
                            (attr.Value is int intValue && intValue == 0) ||
                            (attr.Value is uint uintValue && uintValue == 0) ||
                            (attr.Value is long longValue && longValue == 0) ||
                            (attr.Value is ulong ulongValue && ulongValue == 0) ||
                            (attr.Value is float floatValue && floatValue == 0) ||
                            (attr.Value is double doubleValue && doubleValue == 0)) break;
                        adc.Add(new AnalyzedDataContent(0, attr.Value));
                        estimation += EstimateValueSize_V2(attr.Value, encoding);
                        break;
                }
            }

            if (node.Count <= 0) return estimation;
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var sub_node in node.Children) estimation += Estimate_V2(sub_node, encoding, ref adc);

            return estimation;
        }

        private static void Write_V2(
            this FluxionNode node,
            Stream stream,
            Encoding encoding,
            bool asRoot,
            AnalyzedDataContent[] analyticsData
        )
        {
            encoding = encoding ?? Encoding.Default;

            // Header code that only should be run on Root node.
            if (asRoot)
            {
                // Set node to root.
                node.IsRoot = true;

                // Set node version.
                node.Version = 2;

                // Write FLX on top of the file.
                stream.Write(FluxionMark, 0, FluxionMark.Length);

                // Write version
                stream.WriteByte(node.Version);

                // Write Encoding
                stream.WriteByte(GetEncodingID(encoding));

                // Estimate Data Size
                var dataPos = new List<AnalyzedDataContent>();
                var dataSize = Estimate_V2(node, encoding, ref dataPos);
                var dataEndPos = stream.Position + EstimateValueSize_V2(dataSize, encoding) + dataSize;
                WriteVarLong(stream, dataEndPos);

                var dataEndPos2 = WriteData_V2(stream, encoding, ref dataPos);
                if (dataEndPos2 != dataEndPos)
                    throw new FluxionEstimationError(dataEndPos, dataEndPos2);
                analyticsData = dataPos.ToArray();
            }

            // Get analyzed data for this node.
            AnalyzedDataContent node_ad0 = null;
            AnalyzedDataContent node_an0 = null;
            switch (node.Value)
            {
                default:
                    var analyzedDataContents = analyticsData.Where(it => it.Data == node.Value).ToArray();
                    node_ad0 = analyzedDataContents[0] ?? throw new FluxionAnalyzedDataMissingException();
                    break;

                case null:
                case true:
                case false:
                    break;
            }

            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                var node_an = analyticsData.Where(it => it.Data is string s && s == node.Name).ToArray();
                node_an0 = node_an[0] ?? throw new FluxionAnalyzedDataMissingException();
            }

            // Get value type
            var valueType = GetValueType_V2(node.Value);

            // Check if node has name, no child, no attributes etc. and XOR them to the correct flag.
            if (!string.IsNullOrWhiteSpace(node.Name))
                valueType = (byte)(valueType ^ 16); // Name
            if (node.Count <= 0)
                valueType = (byte)(valueType ^ 32); // No Child
            if (node.Attributes.Count <= 0)
                valueType = (byte)(valueType ^ 64); // No Attributes

            if ((node.Value is byte[] byteArray && byteArray.Length <= 0) ||
                (node.Value is string nodeValueString && string.IsNullOrWhiteSpace(nodeValueString)) ||
                (node.Value is char nodeCharValue && nodeCharValue == char.MinValue) ||
                (node.Value is short nodeShortValue && nodeShortValue <= 0) ||
                (node.Value is ushort nodeUShortValue && nodeUShortValue == 0) ||
                (node.Value is int nodeIntValue && nodeIntValue <= 0) ||
                (node.Value is uint nodeUIntValue && nodeUIntValue == 0) ||
                (node.Value is long nodeLongValue && nodeLongValue <= 0) ||
                (node.Value is ulong nodeULongValue && nodeULongValue == 0))
                valueType = (byte)(valueType ^ 128); // Unique flag

            // Write the type.
            stream.WriteByte(valueType);

            // Node Children count (only if node has children).
            if (node.Count > 0) WriteVarInt(stream, node.Count);

            // Node Name (only if it has one).
            if (!string.IsNullOrWhiteSpace(node.Name) && node_an0 != null)
                WriteVarLong(stream, node_an0.Position);

            // Write data position (if not null, or bool)
            if (node_ad0 != null)
                WriteVarLong(stream, node_ad0.Position);

            if (node.Attributes.Count > 0) WriteVarInt(stream, node.Attributes.Count);

            // Same thing here.
            foreach (FluxionAttribute attr in node.Attributes)
            {
                // Get analyzed data for this attribute.
                AnalyzedDataContent attr_an0 = null;
                AnalyzedDataContent attr_ad0 = null;
                if (!string.IsNullOrWhiteSpace(attr.Name))
                {
                    var attr_an = analyticsData.Where(it => it.Data is string s && s == attr.Name).ToArray();
                    attr_an0 = attr_an[0] ?? throw new FluxionAnalyzedDataMissingException();
                }

                switch (attr.Value)
                {
                    case null:
                    case true:
                    case false:
                        break;

                    default:
                        var attr_ad = analyticsData.Where(it => it.Data == attr.Value).ToArray();
                        attr_ad0 = attr_ad[0] ?? throw new FluxionAnalyzedDataMissingException();
                        break;
                }

                // Get the value type
                var attr_valueType = GetValueType_V2(attr.Value);

                // Check if the node has a name, XOR with 16 to set the flag.
                if (!string.IsNullOrWhiteSpace(attr.Name))
                    attr_valueType = (byte)(attr_valueType ^ 16);

                if ((node.Value is byte[] attr_byteArray && attr_byteArray.Length <= 0) ||
                    (node.Value is string attrValueString && string.IsNullOrWhiteSpace(attrValueString)) ||
                    (node.Value is char attrValueChar && attrValueChar == char.MinValue) ||
                    (node.Value is short attrShortValue && attrShortValue <= 0) ||
                    (node.Value is ushort attrUShortValue && attrUShortValue == 0) ||
                    (node.Value is int attrIntValue && attrIntValue <= 0) ||
                    (node.Value is uint attrUIntValue && attrUIntValue == 0) ||
                    (node.Value is long attrLongValue && attrLongValue <= 0) ||
                    (node.Value is ulong attrULongValue && attrULongValue == 0))
                    valueType = (byte)(valueType ^ 128); // Unique flag

                // Write the value type.
                stream.WriteByte(attr_valueType);

                // Check if attribute has name, if it has one then write position.
                if (!string.IsNullOrWhiteSpace(attr.Name) && attr_an0 != null)
                    WriteVarLong(stream, attr_an0.Position);

                // Write the value position.
                if (attr_ad0 != null)
                    WriteVarLong(stream, attr_ad0.Position);
            }

            // Recursion: Write other nodes (not as root node).
            foreach (var child_node in node.Children)
                Write_V2(child_node, stream, encoding, false, analyticsData);
        }

        #endregion Fluxion 2

        #region Fluxion 1

        private static void Write_V1(
            this FluxionNode node,
            Stream stream,
            Encoding encoding,
            bool asRoot
        )
        {
            encoding = encoding ?? Encoding.Default;
            // Header code that only should be run on Root node.
            if (asRoot)
            {
                // Set node to root.
                node.IsRoot = true;

                // Set node version.
                node.Version = 1;

                // Write FLX to top of the file.
                stream.Write(FluxionMark, 0, FluxionMark.Length);

                // Write version
                stream.WriteByte(node.Version);

                // Write Encoding
                stream.WriteByte(GetEncodingID(encoding));
            }

            // Get value type
            var valueType = GetValueType(node.Value, out var value);

            // Check if node has name, no child, no attributes etc. and XOR them to the correct flag.
            if (!string.IsNullOrWhiteSpace(node.Name))
                valueType = (byte)(valueType ^ 16); // Name
            if (node.Count <= 0)
                valueType = (byte)(valueType ^ 32); // No Child
            if (node.Attributes.Count <= 0)
                valueType = (byte)(valueType ^ 64); // No Attributes

            // Write the type.
            stream.WriteByte(valueType);

            // Node Children count (only if node has children).
            if (node.Count > 0) WriteVarInt(stream, node.Count);

            // Node Name (only if it has one), encoding first then length then name.
            if (!string.IsNullOrWhiteSpace(node.Name))
                WriteByteArrWithVarInt(stream, encoding.GetBytes(node.Name));

            switch (node.Value)
            {
                // Check if the value is string, or byte array for variable-length encoding.
                case string stringValue:
                    WriteByteArrWithVarInt(stream, encoding.GetBytes(stringValue));
                    break;
                case byte[] _:
                    WriteByteArrWithVarInt(stream, value);
                    break;
                default:
                    stream.Write(value, 0, value.Length);
                    break;
            }

            if (node.Attributes.Count > 0) WriteVarInt(stream, node.Attributes.Count);

            // Same thing here.
            foreach (FluxionAttribute attr in node.Attributes)
            {
                // Get value type.
                var attr_valueType = GetValueType(attr.Value, out var attr_value);

                // Check if the node has a name, XOR with 16 to set the flag.
                if (!string.IsNullOrWhiteSpace(attr.Name))
                    attr_valueType = (byte)(attr_valueType ^ 16);

                // Write the value.
                stream.WriteByte(attr_valueType);

                // Check if attribute has name, if it has one then write encoding, length and the name.
                if (!string.IsNullOrWhiteSpace(attr.Name))
                    WriteByteArrWithVarInt(stream, encoding.GetBytes(attr.Name));

                switch (attr.Value)
                {
                    // Check if the value is string, or byte array for variable-length encoding.
                    case string attr_string:
                        WriteByteArrWithVarInt(stream, encoding.GetBytes(attr_string));
                        break;
                    case byte[] att_byte_array:
                        WriteByteArrWithVarInt(stream, att_byte_array);
                        break;
                    default:
                    {
                        if (attr_value.Length > 0) // Only write if value is not null, bool, etc.
                            stream.Write(attr_value, 0, attr_value.Length);
                        break;
                    }
                }
            }

            // Recursion: Write other nodes (not as root node).
            foreach (var child_node in node.Children)
                Write_V1(child_node, stream, encoding, false);
        }

        #endregion Fluxion 1

        #endregion Write

        #region Helpers

        private static object ReadBytesFromType(Stream stream, int valueType, Encoding encoding)
        {
            switch (valueType)
            {
                case 0:
                    return null;
                case 1:
                    return true;
                case 2:
                    return false;
                case 3:
                    var byteValue = stream.ReadByte();
                    if (byteValue == -1)
                        throw new FluxionEndOfStreamException();
                    return (byte)byteValue;
                case 4:
                    var sbyteValue = stream.ReadByte();
                    if (sbyteValue == -1)
                        throw new FluxionEndOfStreamException();
                    return (sbyte)sbyteValue;
                case 5:
                    var charValue = new byte[sizeof(char)];
                    var charRead = stream.Read(charValue, 0, charValue.Length);
                    if (charRead != charValue.Length)
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToChar(charValue, 0);

                case 6:
                    var shortValue = new byte[sizeof(short)];
                    var shortRead = stream.Read(shortValue, 0, shortValue.Length);
                    if (shortRead != sizeof(short))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToInt16(shortValue, 0);
                case 7:
                    var ushortValue = new byte[sizeof(ushort)];
                    var ushortRead = stream.Read(ushortValue, 0, ushortValue.Length);
                    if (ushortRead != sizeof(ushort))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToUInt16(ushortValue, 0);

                case 8:
                    var intValue = new byte[sizeof(int)];
                    var intRead = stream.Read(intValue, 0, intValue.Length);
                    if (intRead != sizeof(int))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToInt32(intValue, 0);
                case 9:
                    var uintValue = new byte[sizeof(uint)];
                    var uintRead = stream.Read(uintValue, 0, uintValue.Length);
                    if (uintRead != sizeof(uint))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToUInt32(uintValue, 0);

                case 10:
                    var longValue = new byte[sizeof(long)];
                    var longRead = stream.Read(longValue, 0, longValue.Length);
                    if (longRead != sizeof(long))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToInt64(longValue, 0);
                case 11:
                    var ulongValue = new byte[sizeof(ulong)];
                    var ulongRead = stream.Read(ulongValue, 0, ulongValue.Length);
                    if (ulongRead != sizeof(ulong))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToUInt64(ulongValue, 0);

                case 12:
                    var floatValue = new byte[sizeof(float)];
                    var floatRead = stream.Read(floatValue, 0, floatValue.Length);
                    if (floatRead != sizeof(float))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToSingle(floatValue, 0);
                case 13:
                    var doubleValue = new byte[sizeof(double)];
                    var doubleRead = stream.Read(doubleValue, 0, doubleValue.Length);
                    if (doubleRead != sizeof(double))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToDouble(doubleValue, 0);

                case 14:
                    return encoding.GetString(DecodeByteArrWithVarInt(stream));

                case 15:
                    return DecodeByteArrWithVarInt(stream);

                default:
                    throw new FluxionValueTypeException((byte)valueType);
            }
        }

        private static object ReadBytesFromType_V2(Stream stream, int valueType, Encoding encoding, bool uniqueFlag)
        {
            var current = stream.Position;
            switch (valueType)
            {
                case 0:
                    if (uniqueFlag) return (short)0;
                    return null;
                case 1:
                    if (uniqueFlag) return 0;
                    return true;
                case 2:
                    if (uniqueFlag) return (long)0;
                    return false;
                case 3:
                    if (uniqueFlag) return (byte)0;
                    var bytePos = DecodeVarLong(stream);
                    stream.Seek(bytePos, SeekOrigin.Begin);
                    var byteValue = stream.ReadByte();
                    if (byteValue == -1)
                        throw new FluxionEndOfStreamException();
                    stream.Seek(current + EstimateValueSize_V2(bytePos, encoding), SeekOrigin.Begin);
                    return (byte)byteValue;
                case 4:
                    if (uniqueFlag) return (sbyte)0;
                    var sbytePos = DecodeVarLong(stream);
                    stream.Seek(sbytePos, SeekOrigin.Begin);
                    var sbyteValue = stream.ReadByte();
                    if (sbyteValue == -1)
                        throw new FluxionEndOfStreamException();
                    stream.Seek(current + EstimateValueSize_V2(sbytePos, encoding), SeekOrigin.Begin);
                    return (sbyte)sbyteValue;
                case 5:
                    if (uniqueFlag) return char.MinValue;
                    var charPos = DecodeVarLong(stream);
                    stream.Seek(charPos, SeekOrigin.Begin);
                    var charValue = char.MinValue;
                    var charShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        charValue |= (char)((b & 0x7F) << charShift);
                        charShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    stream.Seek(current + EstimateValueSize_V2(charPos, encoding), SeekOrigin.Begin);
                    return charValue;

                case 6:
                    var shortPos = DecodeVarLong(stream);
                    stream.Seek(shortPos, SeekOrigin.Begin);
                    short shortValue = 0;
                    var shortShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        shortValue |= (short)((b & 0x7F) << shortShift);
                        shortShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    stream.Seek(current + EstimateValueSize_V2(shortPos, encoding), SeekOrigin.Begin);
                    return uniqueFlag ? (short)-shortValue : shortValue;
                case 7:
                    if (uniqueFlag) return (ushort)0;
                    var ushortPos = DecodeVarLong(stream);
                    stream.Seek(ushortPos, SeekOrigin.Begin);
                    ushort ushortValue = 0;
                    var ushortShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        ushortValue |= (ushort)((b & 0x7F) << ushortShift);
                        ushortShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    stream.Seek(current + EstimateValueSize_V2(ushortPos, encoding), SeekOrigin.Begin);
                    return ushortValue;

                case 8:
                    var intPos = DecodeVarLong(stream);
                    stream.Seek(intPos, SeekOrigin.Begin);
                    var intValue = 0;
                    var intShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        intValue |= (b & 0x7F) << intShift;
                        intShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    stream.Seek(current + EstimateValueSize_V2(intPos, encoding), SeekOrigin.Begin);
                    return uniqueFlag ? -intValue : intValue;
                case 9:
                    if (uniqueFlag) return (uint)0;
                    var uintPos = DecodeVarLong(stream);
                    stream.Seek(uintPos, SeekOrigin.Begin);
                    uint uintValue = 0;
                    var uintShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        uintValue |= (uint)(b & 0x7F) << uintShift;
                        uintShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    stream.Seek(current + EstimateValueSize_V2(uintPos, encoding), SeekOrigin.Begin);
                    return uintValue;

                case 10:
                    var longPos = DecodeVarLong(stream);
                    stream.Seek(longPos, SeekOrigin.Begin);
                    long longValue = 0;
                    var longShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        longValue |= (long)(b & 0x7F) << longShift;
                        longShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    stream.Seek(current + EstimateValueSize_V2(longPos, encoding), SeekOrigin.Begin);
                    return uniqueFlag ? -longValue : longValue;
                case 11:
                    if (uniqueFlag) return (ulong)0;
                    var ulongPos = DecodeVarLong(stream);
                    stream.Seek(ulongPos, SeekOrigin.Begin);
                    ulong ulongValue = 0;
                    var ulongShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        ulongValue |= (ulong)(b & 0x7F) << ulongShift;
                        ulongShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    stream.Seek(current + EstimateValueSize_V2(ulongPos, encoding), SeekOrigin.Begin);
                    return ulongValue;

                case 12:
                    if (uniqueFlag) return 0F;
                    var floatPos = DecodeVarLong(stream);
                    stream.Seek(floatPos, SeekOrigin.Begin);
                    var floatValue = new byte[sizeof(float)];
                    var floatRead = stream.Read(floatValue, 0, floatValue.Length);
                    if (floatRead != sizeof(float))
                        throw new FluxionEndOfStreamException();
                    stream.Seek(current + EstimateValueSize_V2(floatPos, encoding), SeekOrigin.Begin);
                    return BitConverter.ToSingle(floatValue, 0);
                case 13:
                    if (uniqueFlag) return 0D;
                    var doublePos = DecodeVarLong(stream);
                    stream.Seek(doublePos, SeekOrigin.Begin);
                    var doubleValue = new byte[sizeof(double)];
                    var doubleRead = stream.Read(doubleValue, 0, doubleValue.Length);
                    if (doubleRead != sizeof(double))
                        throw new FluxionEndOfStreamException();
                    stream.Seek(current + EstimateValueSize_V2(doublePos, encoding), SeekOrigin.Begin);
                    return BitConverter.ToDouble(doubleValue, 0);

                case 14:
                    if (uniqueFlag) return string.Empty;
                    var stringPos = DecodeVarLong(stream);
                    stream.Seek(stringPos, SeekOrigin.Begin);
                    var stringValue = encoding.GetString(DecodeByteArrWithVarInt(stream));
                    stream.Seek(current + EstimateValueSize_V2(stringPos, encoding), SeekOrigin.Begin);
                    return stringValue;

                case 15:
                    if (uniqueFlag) return Array.Empty<byte>();
                    var byteArrayPos = DecodeVarLong(stream);
                    stream.Seek(byteArrayPos, SeekOrigin.Begin);
                    var byteArrayValue = DecodeByteArrWithVarInt(stream);
                    stream.Seek(current + EstimateValueSize_V2(byteArrayPos, encoding), SeekOrigin.Begin);
                    return byteArrayValue;

                default:
                    throw new FluxionValueTypeException((byte)valueType);
            }
        }

        private static bool isBitSet(byte value, int bitPosition)
        {
            if (bitPosition < 0 || bitPosition > 7)
                throw new ArgumentOutOfRangeException(
                    nameof(bitPosition),
                    "Must be between 0 and 7"
                );
            var bitmask = 1 << bitPosition;
            return (value & bitmask) != 0;
        }

        private static void WriteByteArrWithVarInt(Stream stream, byte[] arr)
        {
            WriteVarInt(stream, arr.Length);

            stream.Write(arr, 0, arr.Length);
        }

        private static void WriteVarInt(Stream stream, int value)
        {
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                b |= (byte)(value > 0 ? 0x80 : 0);
                stream.WriteByte(b);
            } while (value > 0);
        }

        private static void WriteVarLong(Stream stream, long value)
        {
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                b |= (byte)(value > 0 ? 0x80 : 0);
                stream.WriteByte(b);
            } while (value > 0);
        }

        private static byte[] DecodeByteArrWithVarInt(Stream stream)
        {
            var value = DecodeVarInt(stream);

            var valueBytes = new byte[value];
            var valueRead = stream.Read(valueBytes, 0, value);
            if (valueRead != value)
                throw new FluxionEndOfStreamException();
            return valueBytes;
        }

        private static int DecodeVarInt(Stream stream)
        {
            var value = 0;
            var shift = 0;
            byte b;
            do
            {
                b = (byte)stream.ReadByte();
                value |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            return value;
        }

        private static long DecodeVarLong(Stream stream)
        {
            var value = 0;
            var shift = 0;
            byte b;
            do
            {
                b = (byte)stream.ReadByte();
                value |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            return value;
        }

        private static byte GetValueType(object input, out byte[] value)
        {
            byte valueType;
            value = Array.Empty<byte>();
            switch (input)
            {
                case null:
                    valueType = 0;
                    break;
                case true:
                    valueType = 1;
                    break;
                case false:
                    valueType = 2;
                    break;
                case byte byteValue:
                    valueType = 3;
                    value = new[] { byteValue };
                    break;
                case sbyte sbyteValue:
                    valueType = 4;
                    value = new[] { (byte)sbyteValue };
                    break;
                case char charValue:
                    valueType = 5;
                    value = BitConverter.GetBytes(charValue);
                    break;
                case short shortValue:
                    valueType = 6;
                    value = BitConverter.GetBytes(shortValue);
                    break;
                case ushort ushortValue:
                    valueType = 7;
                    value = BitConverter.GetBytes(ushortValue);
                    break;
                case int intValue:
                    valueType = 8;
                    value = BitConverter.GetBytes(intValue);
                    break;
                case uint uintValue:
                    valueType = 9;
                    value = BitConverter.GetBytes(uintValue);
                    break;
                case long longValue:
                    valueType = 10;
                    value = BitConverter.GetBytes(longValue);
                    break;
                case ulong ulongValue:
                    valueType = 11;
                    value = BitConverter.GetBytes(ulongValue);
                    break;
                case float floatValue:
                    valueType = 12;
                    value = BitConverter.GetBytes(floatValue);
                    break;
                case double doubleValue:
                    valueType = 13;
                    value = BitConverter.GetBytes(doubleValue);
                    break;
                case string _:
                    valueType = 14;
                    break;
                case byte[] byteArrayValue:
                    valueType = 15;
                    value = byteArrayValue;
                    break;

                default:
                    throw new FluxionValueTypeException(input.GetType().FullName);
            }

            return valueType;
        }

        private static byte GetValueType_V2(object input)
        {
            byte valueType;
            switch (input)
            {
                case null:
                    valueType = 0;
                    break;
                case true:
                    valueType = 1;
                    break;
                case false:
                    valueType = 2;
                    break;
                case byte _:
                    valueType = 3;
                    break;
                case sbyte _:
                    valueType = 4;
                    break;
                case char _:
                    valueType = 5;
                    break;
                case short shortValue:
                    valueType = shortValue == 0 ? (byte)0 : (byte)6;
                    break;
                case ushort _:
                    valueType = 7;
                    break;
                case int intValue:
                    valueType = intValue == 0 ? (byte)1 : (byte)8;
                    break;
                case uint _:
                    valueType = 9;
                    break;
                case long longValue:
                    valueType = longValue == 0 ? (byte)2 : (byte)10;
                    break;
                case ulong _:
                    valueType = 11;
                    break;
                case float _:
                    valueType = 12;
                    break;
                case double _:
                    valueType = 13;
                    break;
                case string _:
                    valueType = 14;
                    break;
                case byte[] _:
                    valueType = 15;
                    break;

                default:
                    throw new FluxionValueTypeException(input.GetType().FullName);
            }

            return valueType;
        }

        private static int EstimateValueSize_V2(object input, Encoding encoding)
        {
            var bytes = 1;
            switch (input)
            {
                case null:
                case true:
                case false:
                    bytes = 0;
                    break;

                case byte _:
                case sbyte _:
                    bytes = 1;
                    break;

                case char charValue:
                    while ((charValue >>= 7) != 0) bytes++;
                    break;

                case short shortValue:
                    if (shortValue < 0)
                    {
                        shortValue = (short)~shortValue;
                        shortValue++;
                    }

                    while ((shortValue >>= 7) != 0) bytes++;
                    break;

                case ushort ushortValue:
                    while ((ushortValue >>= 7) != 0) bytes++;
                    break;

                case int intValue:
                    if (intValue < 0)
                    {
                        intValue = ~intValue;
                        intValue++;
                    }

                    while ((intValue >>= 7) != 0) bytes++;
                    break;

                case uint uintValue:
                    while ((uintValue >>= 7) != 0) bytes++;
                    break;
                case long longValue:
                    if (longValue < 0)
                    {
                        longValue = ~longValue;
                        longValue++;
                    }

                    while ((longValue >>= 7) != 0) bytes++;
                    break;

                case ulong ulongValue:
                    while ((ulongValue >>= 7) != 0) bytes++;
                    break;

                case float _:
                    bytes = sizeof(float);
                    break;

                case double _:
                    bytes = sizeof(double);
                    break;

                case byte[] byteArrayValue:
                    bytes = EstimateValueSize_V2(byteArrayValue.Length, encoding) + byteArrayValue.Length;
                    break;

                case string stringValue:
                    var encoded = encoding.GetBytes(stringValue);
                    bytes = EstimateValueSize_V2(encoded.Length, encoding) + encoded.Length;
                    break;
            }

            return bytes;
        }

        private static void WriteValue_V2(object input, Stream stream, Encoding encoding)
        {
            switch (input)
            {
                case null:
                case true:
                case false:
                    break;

                case byte byteValue:
                    stream.WriteByte(byteValue);
                    break;

                case sbyte sbyteValue:
                    stream.WriteByte((byte)sbyteValue);
                    break;

                case char charValue:
                    while (true)
                    {
                        var b = (byte)(charValue & 0x7F);
                        charValue >>= 7;

                        if (charValue == 0)
                        {
                            stream.WriteByte(b);
                            break;
                        }

                        stream.WriteByte((byte)(b | 0x80));
                    }

                    break;

                case short shortValue:
                    if (shortValue < 0) shortValue = (short)-shortValue;
                    while (true)
                    {
                        var b = (byte)(shortValue & 0x7F);
                        shortValue >>= 7;

                        if (shortValue == 0)
                        {
                            stream.WriteByte(b);
                            break;
                        }

                        stream.WriteByte((byte)(b | 0x80));
                    }

                    break;

                case ushort ushortValue:
                    while (true)
                    {
                        var b = (byte)(ushortValue & 0x7F);
                        ushortValue >>= 7;

                        if (ushortValue == 0)
                        {
                            stream.WriteByte(b);
                            break;
                        }

                        stream.WriteByte((byte)(b | 0x80));
                    }

                    break;

                case int intValue:
                    if (intValue < 0) intValue = -intValue;
                    while (true)
                    {
                        var b = (byte)(intValue & 0x7F);
                        intValue >>= 7;

                        if (intValue == 0)
                        {
                            stream.WriteByte(b);
                            break;
                        }

                        stream.WriteByte((byte)(b | 0x80));
                    }

                    break;

                case uint uintValue:
                    while (true)
                    {
                        var b = (byte)(uintValue & 0x7F);
                        uintValue >>= 7;

                        if (uintValue == 0)
                        {
                            stream.WriteByte(b);
                            break;
                        }

                        stream.WriteByte((byte)(b | 0x80));
                    }

                    break;

                case long longValue:
                    if (longValue < 0) longValue = -longValue;
                    while (true)
                    {
                        var b = (byte)(longValue & 0x7F);
                        longValue >>= 7;

                        if (longValue == 0)
                        {
                            stream.WriteByte(b);
                            break;
                        }

                        stream.WriteByte((byte)(b | 0x80));
                    }

                    break;

                case ulong ulongValue:
                    while (true)
                    {
                        var b = (byte)(ulongValue & 0x7F);
                        ulongValue >>= 7;

                        if (ulongValue == 0)
                        {
                            stream.WriteByte(b);
                            break;
                        }

                        stream.WriteByte((byte)(b | 0x80));
                    }

                    break;

                case float floatValue:
                    stream.Write(BitConverter.GetBytes(floatValue), 0, sizeof(float));
                    break;

                case double doubleValue:
                    stream.Write(BitConverter.GetBytes(doubleValue), 0, sizeof(double));
                    break;

                case string stringValue:
                    WriteByteArrWithVarInt(stream, encoding.GetBytes(stringValue));
                    break;

                case byte[] byteArrValue:
                    WriteByteArrWithVarInt(stream,byteArrValue);
                    break;

                default:
                    throw new FluxionValueTypeException(input.GetType().FullName);
            }
        }

        #endregion Helpers

        #region Encodings

        private static byte GetEncodingID(this Encoding Encoding)
        {
            switch (Encoding)
            {
                case UTF8Encoding _:
                    return 0;
                case UnicodeEncoding _:
                    return 1;
                case UTF32Encoding _:
                    return 2;
                default:
                    throw new FluxionEncodingException(Encoding);
            }
        }

        private static Encoding GetEncoding(byte value)
        {
            switch (value)
            {
                case 0:
                    return Encoding.UTF8;
                case 1:
                    return Encoding.Unicode;
                case 2:
                    return Encoding.UTF32;

                default:
                    throw new FluxionEncodingException(value);
            }
        }

        #endregion Encodings
    }

    #endregion Fluxion

    #region Node

    public abstract class FluxionObject
    {
        /// <summary>
        ///     Parent of this node.
        /// </summary>
        public FluxionNode Parent { get; internal set; }

        /// <summary>
        ///     Name of the node.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     Value of this node. Currently, these types are supported:
        ///     <para />
        ///     <c>null</c>, <c>true</c>, <c>false</c>, <see cref="byte" />, <see cref="sbyte" />, <see cref="char" />,
        ///     <see cref="short" />, <see cref="ushort" />, <see cref="int" />, <see cref="uint" />, <see cref="long" />,
        ///     <see cref="ulong" />, <see cref="float" />, <see cref="double" />, <see cref="string" />, <c>byte[]</c>.
        /// </summary>
        public object Value { get; set; }
    }

    /// <summary>
    ///     A Fluxion node class.
    /// </summary>
    public class FluxionNode : FluxionObject
    {
        private readonly List<FluxionNode> _children = new List<FluxionNode>();
        private byte _version = Fluxion.Version;

        /// <summary>
        ///     Determines if a node is root node or not.
        /// </summary>
        public bool IsRoot { get; internal set; }

        /// <summary>
        ///     Gets the root node.
        /// </summary>
        // ReSharper disable once UnusedMember.Global
        public FluxionNode Root => IsRoot ? this : Parent.Root;

        /// <summary>
        ///     Gets/sets the Fluxion version for this node.
        /// </summary>
        public byte Version
        {
            get => IsRoot ? _version : Parent.Version;
            set
            {
                _version = value;
                if (!IsRoot)
                    Parent.Version = value;
            }
        }


        /// <summary>
        ///     Collection of the children in this node.
        /// </summary>
        // ReSharper disable once MemberCanBePrivate.Global
        public FluxionNode[] Children

        {
            get
            {
                var children = new FluxionNode[_children.Count];
                for (var i = 0; i < children.Length; i++) children[i] = this[i];

                return children;
            }
        }

        /// <summary>
        ///     Attributes of this node.
        /// </summary>
        public FluxionAttributeCollection Attributes { get; internal set; } = new FluxionAttributeCollection();

        /// <summary>
        ///     Gets a children from index.
        /// </summary>
        /// <param name="index">Index of the children.</param>

        // ReSharper disable once UnusedMember.Global
        // ReSharper disable once MemberCanBePrivate.Global
        public FluxionNode this[int index] => _children[index];

        /// <summary>
        ///     Gets a children from name.
        /// </summary>
        /// <param name="name">Name of the children.</param>

        // ReSharper disable once UnusedMember.Global
        public FluxionNode this[string name]
        {
            get
            {
                foreach (var t in _children)
                {
                    var node = t;
                    if (string.Equals(node.Name, name))
                        return node;
                }

                return null;
            }
        }

        /// <summary>
        ///     Gets the total amount of child nodes of this node.
        /// </summary>
        public int Count => _children.Count;

        /// <summary>
        ///     Gets the index of a node.
        /// </summary>
        /// <param name="node">Node to get the index of.</param>
        /// <returns>Index of <paramref name="node" />.</returns>
        // ReSharper disable once UnusedMember.Global
        public int IndexOf(FluxionNode node)
        {
            if (node != null)
                return _children.IndexOf(node);
            return -1;
        }

        /// <summary>
        ///     Adds a node to collection.
        /// </summary>
        /// <param name="node">Node to add.</param>
        /// <returns>Index of the node.</returns>
        // ReSharper disable once UnusedMethodReturnValue.Global
        public int Add(FluxionNode node)
        {
            if (node == null) return -1;
            if (Parent == node || CheckIfNodeIsInTree(node, Parent))
                throw new FluxionParentException();
            node.Parent?.Remove(node);
            node.Parent = Parent;
            _children.Add(node);
            return _children.Count - 1;
        }

        /// <summary>
        ///     Removes a node from collection.
        /// </summary>
        /// <param name="node">Node to remove.</param>
        // ReSharper disable once UnusedMember.Global
        // ReSharper disable once MemberCanBePrivate.Global
        public void Remove(FluxionNode node)
        {
            if (node.Parent == Parent)
                node.Parent = null;
            _children.Remove(node);
        }

        /// <summary>
        ///     Adds range of nodes into collection.
        /// </summary>
        /// <param name="nodes">Nodes to add.</param>
        // ReSharper disable once UnusedMember.Global
        public void AddRange(FluxionNode[] nodes)
        {
            if (nodes == null)
                return;
            foreach (var t in nodes)
                if (Parent == t || CheckIfNodeIsInTree(t, Parent))
                {
                    throw new FluxionParentException();
                }
                else
                {
                    t.Parent?.Remove(t);
                    t.Parent = Parent;
                }

            _children.AddRange(nodes);
        }

        /// <summary>
        ///     Inserts a node into a specific index.
        /// </summary>
        /// <param name="index">Index to insert.</param>
        /// <param name="node">Node to insert.</param>
        // ReSharper disable once UnusedMember.Global
        public void Insert(int index, FluxionNode node)
        {
            if (index > _children.Count || node == null)
                return;
            if (Parent == node || CheckIfNodeIsInTree(node, Parent))
                throw new FluxionParentException();
            node.Parent?.Remove(node);
            node.Parent = Parent;
            _children.Insert(index, node);
        }

        /// <summary>
        ///     Checks if a node is in the collection.
        /// </summary>
        /// <param name="node">Node to check.</param>
        /// <returns>True if the node is in this collection. Otherwise, false.</returns>
        // ReSharper disable once UnusedMember.Global
        // ReSharper disable once MemberCanBePrivate.Global
        public bool Contains(FluxionNode node)
        {
            return _children.Contains(node);
        }

        #region Internal Code

        private static bool CheckIfNodeIsInTree(FluxionNode node, FluxionNode new_parent)
        {
            return node.Count > 0
                   && (
                       node.Contains(new_parent)
                       || node.Children
                           .Any(children => CheckIfNodeIsInTree(children, new_parent))
                   );
        }

        #endregion Internal Code
    }

    #endregion Node

    #region Attributes

    /// <summary>
    ///     A Fluxion Node Attribute Class.
    /// </summary>
    public class FluxionAttribute : FluxionObject
    {
    }

    /// <summary>
    ///     A collection of attributes for a node.
    /// </summary>
    public class FluxionAttributeCollection : CollectionBase
    {
        /// <summary>
        ///     Creates a new collection.
        /// </summary>
        public FluxionAttributeCollection()
        {
        }

        /// <summary>
        ///     Creates a new collection with items.
        /// </summary>
        /// <param name="attributes">Attributes themselves.</param>
        // ReSharper disable once UnusedMember.Global
        public FluxionAttributeCollection(params FluxionAttribute[] attributes)
            : this()
        {
            InnerList.AddRange(attributes);
        }

        /// <summary>
        ///     Gets a specific attribute with index.
        /// </summary>
        /// <param name="index">Index of the attribute.</param>
        public FluxionAttribute this[int index] =>
            List[index] is FluxionAttribute attr ? attr : null;

        /// <summary>
        ///     Gets a specific attribute with name.
        /// </summary>
        /// <param name="name">Name of the attribute.</param>
        public FluxionAttribute this[string name]
        {
            get
            {
                foreach (var t in List)
                {
                    var attr = (FluxionAttribute)t;
                    if (string.Equals(attr.Name, name))
                        return attr;
                }

                return null;
            }
        }

        /// <summary>
        ///     Gets the index of an attribute.
        /// </summary>
        /// <param name="attribute">Attribute to check the index of.</param>
        /// <returns>Index of <paramref name="attribute" />.</returns>
        // ReSharper disable once UnusedMember.Global
        public int IndexOf(FluxionAttribute attribute)
        {
            if (attribute != null)
                return List.IndexOf(attribute);
            return -1;
        }

        /// <summary>
        ///     Adds an attribute to collection.
        /// </summary>
        /// <param name="attribute">Attribute to add.</param>
        /// <returns>Index of the attribute.</returns>
        // ReSharper disable once UnusedMethodReturnValue.Global
        public int Add(FluxionAttribute attribute)
        {
            if (attribute == null)
                return -1;
            return List.Add(attribute);
        }

        /// <summary>
        ///     Removes an attribute from collection.
        /// </summary>
        /// <param name="attribute">Attribute to remove.</param>
        // ReSharper disable once UnusedMember.Global
        public void Remove(FluxionAttribute attribute)
        {
            InnerList.Remove(attribute);
        }

        /// <summary>
        ///     Adds a range of attributes to collection.
        /// </summary>
        /// <param name="attributes">Attributes to add.</param>
        // ReSharper disable once UnusedMember.Global
        public void AddRange(FluxionAttribute[] attributes)
        {
            if (attributes == null)
                return;
            InnerList.AddRange(attributes);
        }

        /// <summary>
        ///     Inserts an attribute to specific index.
        /// </summary>
        /// <param name="index">Index to insert to.</param>
        /// <param name="attribute">Attribute to insert.</param>
        // ReSharper disable once UnusedMember.Global
        public void Insert(int index, FluxionAttribute attribute)
        {
            if (index > List.Count || attribute == null)
                return;
            List.Insert(index, attribute);
        }

        /// <summary>
        ///     Checks if an attribute exists in this collection.
        /// </summary>
        /// <param name="attribute">Attribute to check.</param>
        /// <returns>True if the attribute exists. Otherwise, false.</returns>
        // ReSharper disable once UnusedMember.Global
        public bool Contains(FluxionAttribute attribute)
        {
            return List.Contains(attribute);
        }
    }

    #endregion Attributes

    #region Exceptions

    namespace Exceptions
    {
        /// <summary>
        ///     Base exception class for all Fluxion-related exceptions.
        /// </summary>
        public class FluxionException : Exception
        {
            /// <summary>
            ///     Creates an exception.
            /// </summary>
            /// <param name="message">Message of the exception.</param>
            protected FluxionException(string message)
                : base(message)
            {
            }
        }

        /// <summary>
        ///     Exception to throw when Fluxion header is invalid.
        /// </summary>
        public class FluxionInvalidHeaderException : FluxionException
        {
            /// <summary>
            ///     Throws an exception, telling this Fluxion data does not have a proper header.
            /// </summary>
            internal FluxionInvalidHeaderException()
                : base("Header does not contain valid FLX mark.")
            {
            }
        }

        /// <summary>
        ///     Exception to throw when end of a stream is reached (usually, raised when stream reads -1).
        /// </summary>
        public class FluxionEndOfStreamException : FluxionException
        {
            /// <summary>
            ///     Throws an exception to tell the stream has ended while Fluxion expected more data.
            /// </summary>
            internal FluxionEndOfStreamException()
                : base("End of the stream reached prematurely.")
            {
            }
        }

        /// <summary>
        ///     Exception to throw when an unknown encoding is used on a Fluxion file.
        /// </summary>
        public class FluxionEncodingException : FluxionException
        {
            /// <summary>
            ///     Throw an exception top tell this encoding is not available yet.
            /// </summary>
            /// <param name="encoding">Encoding that has not implemented yet.</param>
            internal FluxionEncodingException(Encoding encoding)
                : base($"Encoding \"{encoding.EncodingName}\" is not implemented.")
            {
            }

            /// <summary>
            ///     Throw an exception top tell this encoding is not available yet.
            /// </summary>
            /// <param name="encoding">Encoding that has not implemented yet.</param>
            internal FluxionEncodingException(byte encoding)
                : base($"Encoding ID \"{encoding}\" is not implemented.")
            {
            }
        }

        /// <summary>
        ///     Exception to throw if a type of value is not implemented yet.
        /// </summary>
        public class FluxionValueTypeException : FluxionException
        {
            /// <summary>
            ///     Throws an exception telling a specific type is not implemented.
            /// </summary>
            /// <param name="type">Type that is not implemented.</param>
            internal FluxionValueTypeException(string type)
                : base($"Value type \"{type}\" is not implemented.")
            {
            }

            /// <summary>
            ///     Throws an exception telling a specific type with ID is not implemented.
            /// </summary>
            /// <param name="id">ID of a value that isn't implemented yet.</param>
            internal FluxionValueTypeException(byte id)
                : base($"Value type with ID \"{id}\" is not implemented.")
            {
            }
        }

        /// <summary>
        ///     Exception to throw if a node is attempted to add to itself or to the same tree.
        /// </summary>
        public class FluxionParentException : FluxionException
        {
            /// <summary>
            ///     Throws an exception when a node is attempted to add to itself or to the same tree.
            /// </summary>
            internal FluxionParentException()
                : base("Cannot add node to self or into the same tree.")
            {
            }
        }

        /// <summary>
        ///     Exception to throw if a node is made for a Fluxion version that isn't supported by this library (ex. future
        ///     versions
        ///     of Fluxion) at the moment.
        /// </summary>
        public class FluxionUnsupportedVersionException : FluxionException
        {
            /// <summary>
            ///     Throws an exception telling a node is made for a Fluxion version that isn't supported by this library (ex. future
            ///     versions of Fluxion) at the moment.
            /// </summary>
            /// <param name="version">Version that isn't supported.</param>
            internal FluxionUnsupportedVersionException(byte version)
                : base(
                    $"Version \"{version}\" is currently not supported by Fluxion. Please update your FluxionSharp to a newer version."
                )
            {
            }
        }

        /// <summary>
        ///     Exception to throw when something went wrong with the Fluxion library code and called for operation that shouldn't
        ///     be called (ex. writing byte array from WriteValue functions).
        /// </summary>
        public class FluxionInvalidCallException : FluxionException
        {
            /// <summary>
            ///     Throws an exception where something went wrong with the Fluxion library code and called for operation that
            ///     shouldn't be called (ex. writing byte array from WriteValue functions).
            /// </summary>
            internal FluxionInvalidCallException() : base("Invalid call.")
            {
            }
        }

        /// <summary>
        ///     Exception to throw if analyzed data is missing while writing Fluxion nodes starting in v2.
        /// </summary>
        public class FluxionAnalyzedDataMissingException : FluxionException
        {
            /// <summary>
            ///     Throws an exception where analyzed data is missing while writing Fluxion nodes starting in v2.
            /// </summary>
            internal FluxionAnalyzedDataMissingException() : base("Analyzed data is missing.")
            {
            }
        }

        /// <summary>
        ///     Exception to throw if estimated data length is different then actual data length.
        /// </summary>
        public class FluxionEstimationError : FluxionException
        {
            /// <summary>
            ///     Throws an exception when estimated data length is different then actual data length.
            /// </summary>
            internal FluxionEstimationError(long expected, long received) : base(
                $"Estimated data length (\"{expected}\") is not same as actual data length (\"{received}\").")
            {
            }
        }
    }

    #endregion Exceptions
}
