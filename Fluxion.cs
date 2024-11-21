using System;
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
        public const byte Version = 3;

        // ReSharper disable once MemberCanBePrivate.Global
        public static readonly byte[] FluxionMark = { 0x46, 0x4c, 0x58 };

        #endregion

        #region Read

        /// <summary>
        ///     Read a Fluxion formatted file or stream.
        /// </summary>
        /// <param name="options">Options for reading.</param>
        /// <returns>A root <see cref="FluxionNode" />.</returns>
        /// <exception cref="FluxionInvalidHeaderException">
        ///     Exception thrown if the <paramref name="options.Stream" /> does not start with
        ///     "FLX".
        /// </exception>
        /// <exception cref="FluxionEndOfStreamException">
        ///     Exception thrown if the end of the stream is reached but expected more
        ///     data.
        /// </exception>
        /// <exception cref="FluxionException">
        /// Other misc. exceptions.
        /// </exception>
        public static FluxionNode Read(FluxionReadOptions options)
        {
            if (options.Stream is null)
            {
                if (string.IsNullOrWhiteSpace(options.File) || !File.Exists(options.File))
                    throw new FileNotFoundException(options.File);
                options.Stream = new FileStream(options.File, options.FileMode, options.FileAccess, options.FileShare);
            }

            var root = new FluxionNode { IsRoot = true, Version = Version };

            var byteF = options.Stream.ReadByte();
            var byteL = options.Stream.ReadByte();
            var byteX = options.Stream.ReadByte();

            if (byteF != FluxionMark[0] && byteL != FluxionMark[1] && byteX != FluxionMark[2])
                throw new FluxionInvalidHeaderException();

            var versionByte = options.Stream.ReadByte();
            if (versionByte == -1)
                throw new FluxionEndOfStreamException();


            switch (versionByte)
            {
                case 1:
                {
                    var encodingByte = options.Stream.ReadByte();
                    if (encodingByte == -1)
                        throw new FluxionEndOfStreamException();
                    root = ReadRecurse_V1(options, GetEncoding((byte)encodingByte), root, true);
                }
                    break;
                case 2:
                {
                    var encodingByte = options.Stream.ReadByte();
                    if (encodingByte == -1)
                        throw new FluxionEndOfStreamException();
                    root = ReadRecurse_V2(options, GetEncoding((byte)encodingByte), root, true);
                }
                    break;

                case 3:
                    root = ReadV3(options);
                    break;

                default:
                    throw new FluxionUnsupportedVersionException(root.Version);
            }

            root.Version = (byte)versionByte;
            return root;
        }

        #region Fluxion 3

        private static FluxionNode ReadV3(FluxionReadOptions options)
        {
            var itemCount = DecodeVarInt(options.Stream);
            var dataCount = DecodeVarInt(options.Stream);
            var dataArray = new object[dataCount];
            var f3Items = new FluxionItem[itemCount];

            for (var i = 0; i < dataCount; i++)
            {
                var dataType = options.Stream.ReadByte();
                dataArray[i] = ReadBytesFromType_V3(options.Stream, dataType);
            }

            for (var i = 0; i < f3Items.Length; i++)
            {
                FluxionItem result;
                var type = options.Stream.ReadByte();
                if (type < 0) throw new FluxionEndOfStreamException();
                var isReference = IsBitSet(type, 0);
                var isAttribute = IsBitSet(type, 1);
                var hasName = IsBitSet(type, 2);
                var hasValue = IsBitSet(type, 3);
                var hasChildren = false;
                var hasAttributes = false;
                var refId = -1;
                var refCount = 1;
                if (isReference)
                {
                    refId = DecodeVarInt(options.Stream);
                    refCount = DecodeVarInt(options.Stream);
                }

                if (isAttribute)
                {
                    if (isReference && f3Items[refId] is FluxionAttribute refAttr)
                        result = refAttr;
                    else
                        result = new FluxionAttribute();

                    result.ValueType = (byte)(type / 16);
                }
                else
                {
                    hasChildren = IsBitSet(type, 4);
                    hasAttributes = IsBitSet(type, 6);
                    if (isReference && f3Items[refId] is FluxionNode refNode)
                        result = refNode.Clone(true, true, IsBitSet(type, 7), IsBitSet(type, 5));
                    else
                        result = new FluxionNode();
                }

                result.ReferenceCount = refCount;

                if (hasName)
                {
                    result.NameId = DecodeVarInt(options.Stream);
                    if (dataArray[result.NameId] is string n) result.Name = n;
                }

                if (hasValue)
                {
                    if (!isAttribute)
                    {
                        var valueType = options.Stream.ReadByte();
                        if (valueType < 0) throw new FluxionEndOfStreamException();
                        result.ValueType = (byte)valueType;
                    }

                    result.ValueId = DecodeVarInt(options.Stream);
                    var data = dataArray[result.ValueId];
                    if (GetValueType_V2(data) != result.ValueType)
                        throw new FluxionValueTypeMismatchException(result.ValueType, GetValueType_V2(data));
                    result.Value = data;
                }

                if (!isAttribute && result is FluxionNode node)
                {
                    if (hasChildren)
                    {
                        var childType = options.Stream.ReadByte();
                        if (childType < 0) throw new FluxionEndOfStreamException();
                        switch (childType)
                        {
                            case 0:
                                var count = DecodeVarInt(options.Stream);
                                for (var ci = 0; ci < count; ci++)
                                    if (f3Items[DecodeVarInt(options.Stream)] is FluxionNode child)
                                        node.Add(child);
                                break;
                            case 1:
                                var smallestItem = DecodeVarInt(options.Stream);
                                var biggestItem = DecodeVarInt(options.Stream);
                                for (var ci = smallestItem; ci < biggestItem; ci++)
                                    if (f3Items[ci] is FluxionNode child)
                                        node.Add(child);
                                break;
                        }
                    }

                    if (hasAttributes)
                    {
                        var attrType = options.Stream.ReadByte();
                        if (attrType < 0) throw new FluxionEndOfStreamException();
                        switch (attrType)
                        {
                            case 0:
                                var count = DecodeVarInt(options.Stream);
                                for (var ai = 0; ai < count; ai++)
                                    if (f3Items[DecodeVarInt(options.Stream)] is FluxionAttribute attribute)
                                        node.Attributes.Add(attribute);
                                break;

                            case 1:
                                var smallestItem = DecodeVarInt(options.Stream);
                                var biggestItem = DecodeVarInt(options.Stream);
                                for (var ai = smallestItem; ai < biggestItem; ai++)
                                    if (f3Items[ai] is FluxionAttribute attribute)
                                        node.Attributes.Add(attribute);
                                break;
                        }
                    }
                }

                for (var di = 0; di < result.ReferenceCount; di++)
                {
                    f3Items[i] = result.FullClone();
                    if (result.ReferenceCount > 1 && di != result.ReferenceCount - 1) i++;
                }
            }

            var rootId = DecodeVarInt(options.Stream);
            if (!(f3Items[rootId] is FluxionNode root))
                throw new FluxionUnexpectedItemTypeException(rootId, "node");
            root.IsRoot = true;
            return root;
        }

        #endregion Fluxion 3

        #region Fluxion 2

        private static FluxionNode ReadRecurse_V2(
            FluxionReadOptions options,
            Encoding encoding,
            FluxionNode rootNode,
            bool readRoot = false
        )
        {
            if (readRoot)
            {
                var treeMarkStart = DecodeVarLong(options.Stream);
                options.Stream.Seek(treeMarkStart, SeekOrigin.Begin);
            }

            var node = readRoot ? rootNode : new FluxionNode { IsRoot = false, Parent = rootNode };

            var valueType = options.Stream.ReadByte();
            if (valueType == -1)
                throw new FluxionEndOfStreamException();

            var hasName = IsBitSet((byte)valueType, 4);
            valueType -= hasName ? 16 : 0;
            var noChild = IsBitSet((byte)valueType, 5);
            valueType -= noChild ? 32 : 0;
            var noAttr = IsBitSet((byte)valueType, 6);
            valueType -= noAttr ? 64 : 0;
            var uniqueFlag = IsBitSet((byte)valueType, 7);
            valueType -= uniqueFlag ? 128 : 0;

            var childrenCount = 0;
            if (!noChild) childrenCount = DecodeVarInt(options.Stream);

            if (hasName)
            {
                var namePos = DecodeVarLong(options.Stream);
                var pos = options.Stream.Position;
                options.Stream.Seek(namePos, SeekOrigin.Begin);
                node.Name = encoding.GetString(DecodeByteArrWithVarInt(options.Stream));
                options.Stream.Seek(pos, SeekOrigin.Begin);
            }

            node.Value = ReadBytesFromType_V2(options.Stream, valueType, encoding, uniqueFlag);

            if (!noAttr)
            {
                var attrCount = DecodeVarInt(options.Stream);

                for (var i = 0; i < attrCount; i++)
                {
                    var attr = new FluxionAttribute();
                    var attrValueType = options.Stream.ReadByte();
                    if (attrValueType == -1)
                        throw new FluxionEndOfStreamException();

                    var attrHasName = IsBitSet((byte)attrValueType, 4);
                    attrValueType -= attrHasName ? 16 : 0;

                    var attrUniqueFlag = IsBitSet((byte)attrValueType, 7);
                    valueType -= attrUniqueFlag ? 128 : 0;

                    if (attrHasName)
                    {
                        var namePos = DecodeVarLong(options.Stream);
                        var pos = options.Stream.Position;
                        options.Stream.Seek(namePos, SeekOrigin.Begin);
                        attr.Name = encoding.GetString(DecodeByteArrWithVarInt(options.Stream));
                        options.Stream.Seek(pos, SeekOrigin.Begin);
                    }

                    attr.Value = ReadBytesFromType_V2(options.Stream, attrValueType, encoding, attrUniqueFlag);

                    node.Attributes.Add(attr);
                }
            }

            if (noChild) return node;

            for (var i = 0; i < childrenCount; i++)
                node.Add(ReadRecurse_V2(options, encoding, node));


            return node;
        }

        #endregion Fluxion 2

        #region Fluxion 1

        private static FluxionNode ReadRecurse_V1(
            FluxionReadOptions options,
            Encoding encoding,
            FluxionNode rootNode,
            bool readRoot = false
        )
        {
            var node = readRoot ? rootNode : new FluxionNode { IsRoot = false, Parent = rootNode };

            var valueType = options.Stream.ReadByte();
            if (valueType == -1)
                throw new FluxionEndOfStreamException();

            // Check flags
            var hasName = IsBitSet((byte)valueType, 4);
            valueType -= hasName ? 16 : 0;
            var noChild = IsBitSet((byte)valueType, 5);
            valueType -= noChild ? 32 : 0;
            var noAttr = IsBitSet((byte)valueType, 6);
            valueType -= noAttr ? 64 : 0;

            // Get Child Count
            var childrenCount = 0;
            if (!noChild) childrenCount = DecodeVarInt(options.Stream);

            if (hasName) node.Name = encoding.GetString(DecodeByteArrWithVarInt(options.Stream));

            // Read value here
            node.Value = ReadBytesFromType(options.Stream, valueType, encoding);

            if (!noAttr)
            {
                var attrCount = DecodeVarInt(options.Stream);

                for (var i = 0; i < attrCount; i++)
                {
                    var attr = new FluxionAttribute();
                    var attrValueType = options.Stream.ReadByte();
                    if (attrValueType == -1)
                        throw new FluxionEndOfStreamException();

                    var attrHasName = IsBitSet((byte)attrValueType, 4);
                    attrValueType -= attrHasName ? 16 : 0;

                    if (attrHasName)
                    {
                        var attrNameBytes = DecodeByteArrWithVarInt(options.Stream);
                        attr.Name = encoding.GetString(attrNameBytes);
                    }

                    attr.Value = ReadBytesFromType(options.Stream, attrValueType, encoding);

                    node.Attributes.Add(attr);
                }
            }

            if (noChild) return node;
            {
                for (var i = 0; i < childrenCount; i++)
                    node.Add(ReadRecurse_V1(options, encoding, node));
            }

            return node;
        }

        #endregion Fluxion 1

        #endregion Read

        #region Write

        /// <summary>
        ///     Writes a Fluxion node to a file.
        /// </summary>
        /// <param name="node">Node to write.</param>
        /// <param name="options">Define options here.</param>
        // ReSharper disable once UnusedMember.Global
        public static void Write(
            this FluxionNode node, FluxionWriteOptions options
        )
        {
            if (options.Stream is null)
                options.Stream = File.Exists(options.File)
                    ? new FileStream(options.File, options.FileMode, options.FileAccess, options.FileShare)
                    : File.Create(options.File);
            while (true)
            {
                switch (options.Version)
                {
                    case 0:
                        options.Version = Version;
                        continue;

                    case 1:
                        Write_V1(node, options, options.Encoding, true);
                        break;

                    case 2:
                        Write_V2(node, options, options.Encoding, true, Array.Empty<AnalyzedDataContent>());
                        break;

                    case 3:
                        Write_V3(node, options);
                        break;

                    default:
                        throw new FluxionUnsupportedVersionException(options.Version);
                }

                break;
            }
        }

        #region Fluxion 3

        internal static bool IsEqual(object a, object b, float floatTolerance = 0.001F, double doubleTolerance = 0.001D)
        {
            switch (a)
            {
                case null:
                    return b is null;
                case bool boolValue1:
                    return b is bool boolValue2 && boolValue1 == boolValue2;
                case byte byteValue1:
                    return b is byte byteValue2 && byteValue1 == byteValue2;
                case sbyte sbyteValue1:
                    return b is sbyte sbyteValue2 && sbyteValue1 == sbyteValue2;
                case char charValue1:
                    return b is char charValue2 && charValue1 == charValue2;
                case short shortValue1:
                    return b is short shortValue2 && shortValue1 == shortValue2;
                case ushort ushortValue1:
                    return b is ushort ushortValue2 && ushortValue1 == ushortValue2;
                case int intValue1:
                    return b is int intValue2 && intValue1 == intValue2;
                case uint uintValue1:
                    return b is uint uintValue2 && uintValue1 == uintValue2;
                case long longValue1:
                    return b is long longValue2 && longValue1 == longValue2;
                case ulong ulongValue1:
                    return b is ulong ulongValue2 && ulongValue1 == ulongValue2;
                case float floatValue1:
                    return b is float floatValue2 && Math.Abs(floatValue1 - floatValue2) < floatTolerance;
                case double doubleValue1:
                    return b is double doubleValue2 && Math.Abs(doubleValue1 - doubleValue2) < doubleTolerance;
                case string stringValue1:
                    return b is string stringValue2 && string.Equals(stringValue1, stringValue2);
                case byte[] byteArrayValue1:
                    if (!(b is byte[] byteArrayValue2)) return false;
                    if (byteArrayValue1.Length != byteArrayValue2.Length) return false;
                    for (var i = 0; i < byteArrayValue1.Length; i++)
                        if (byteArrayValue1[i] != byteArrayValue2[i])
                            return false;
                    return true;
                default:
                    return false;
            }
        }


        #region Analyze Helpers

        private static bool ValueIsUnique(object value)
        {
            return (value is byte[] byteArray && byteArray.Length <= 0) ||
                   (value is string nodeValueString && string.IsNullOrWhiteSpace(nodeValueString)) ||
                   (value is char nodeCharValue && nodeCharValue == char.MinValue) ||
                   (value is short nodeShortValue && nodeShortValue <= 0) ||
                   (value is ushort nodeUShortValue && nodeUShortValue == 0) ||
                   (value is int nodeIntValue && nodeIntValue <= 0) ||
                   (value is uint nodeUIntValue && nodeUIntValue == 0) ||
                   (value is long nodeLongValue && nodeLongValue <= 0) ||
                   (value is ulong nodeULongValue && nodeULongValue == 0);
        }

        #endregion Analyze Helpers

        #region AnaylzeV3

        private static void CountV3(FluxionNode node, out int nodeCount, out int attributeCount)
        {
            nodeCount = 1;
            attributeCount = node.Attributes.Count;

            if (node.Count <= 0) return;
            for (var i = 0; i < node.Count; i++)
            {
                CountV3(node[i], out var nC, out var aC);
                nodeCount += nC;
                attributeCount += aC;
            }
        }

        private static int ListV3(FluxionNode node, FluxionWriteOptions options, ref FluxionItem[] f3,
            ref List<object> data, ref int p)
        {
            var attrArray = new int[node.Attributes.Count];
            for (var i = 0; i < node.Attributes.Count; i++)
            {
                var a = node.Attributes[i];
                var attrNameSkipAdd = false;
                var attrNamePos = -1;
                var attrValueSkipAdd = false;
                var attrValuePos = -1;
                if (!string.IsNullOrWhiteSpace(a.Name))
                {
                    for (var ni = 0; ni < data.Count; ni++)
                    {
                        if (!(data[ni] is string name) || name != a.Name) continue;
                        attrNamePos = ni;
                        attrNameSkipAdd = true;
                        break;
                    }

                    if (!attrNameSkipAdd)
                    {
                        data.Add(a.Name);
                        attrNamePos = data.Count - 1;
                    }
                }

                if (a.Value != null)
                {
                    for (var vi = 0; vi < data.Count; vi++)
                    {
                        if (!IsEqual(data[vi], a.Value, options.FloatTolerance, options.DoubleTolerance)) continue;
                        attrValueSkipAdd = true;
                        attrValuePos = vi;
                        break;
                    }

                    if (!attrValueSkipAdd)
                    {
                        data.Add(a.Value);
                        attrValuePos = data.Count - 1;
                    }
                }

                f3[p] = a;
                f3[p].ReferenceCount = 1;
                f3[p].ReferenceId = -1;
                f3[p].CopyAttributes = false;
                f3[p].CopyChildren = false;
                f3[p].NameId = attrNamePos;
                f3[p].ValueId = attrValuePos;
                f3[p].ValueType = GetValueType_V2(a.Value);
                attrArray[i] = p;
                p++;
            }

            var nodesArray = new int[node.Count];
            for (var i = 0; i < node.Children.Count; i++)
            {
                var childNode = node.Children[i];
                nodesArray[i] = ListV3(childNode, options, ref f3, ref data, ref p);
            }

            var nameSkipAdd = false;
            var namePos = -1;
            var valueSkipAdd = false;
            var valuePos = -1;

            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                for (var ni = 0; ni < data.Count; ni++)
                {
                    if (!(data[ni] is string name) || name != node.Name) continue;
                    nameSkipAdd = true;
                    namePos = ni;
                    break;
                }

                if (!nameSkipAdd)
                {
                    data.Add(node.Name);
                    namePos = data.Count - 1;
                }
            }

            if (node.Value != null)
            {
                for (var vi = 0; vi < data.Count; vi++)
                {
                    if (!IsEqual(data[vi], node.Value, options.FloatTolerance, options.DoubleTolerance)) continue;
                    valueSkipAdd = true;
                    valuePos = vi;
                    break;
                }

                if (!valueSkipAdd)
                {
                    data.Add(node.Value);
                    valuePos = data.Count - 1;
                }
            }

            f3[p] = node;
            f3[p].ReferenceCount = 1;
            f3[p].ReferenceId = -1;
            f3[p].CopyAttributes = false;
            f3[p].CopyChildren = false;
            f3[p].NameId = namePos;
            f3[p].ValueId = valuePos;
            f3[p].ValueType = GetValueType_V2(node.Value);
            f3[p].ChildrenIDs = nodesArray;
            f3[p].AttributesIDs = attrArray;
            p++;
            return p - 1;
        }

        private static FluxionItem[] OptimizeV3(FluxionWriteOptions options, FluxionItem[] list)
        {
            var uniqueItems = new List<FluxionItem>();

            foreach (var item in list)
            {
                var existingItem = uniqueItems.LastOrDefault(existing =>
                    existing.IsDeepEqual(item, options.FloatTolerance, options.DoubleTolerance));

                if (existingItem != null)
                {
                    if (existingItem.IsReference)
                    {
                        existingItem.ReferenceCount++;
                        continue;
                    }

                    item.IsReference = true;
                    item.ReferenceId = uniqueItems.IndexOf(existingItem);
                }

                uniqueItems.Add(item);
            }

            return uniqueItems.ToArray();
        }

        #endregion AnaylzeV3


        private static void Write_V3(FluxionNode root, FluxionWriteOptions options)
        {
            // Set node to root.
            root.IsRoot = true;

            // Set node version.
            root.Version = 3;

            // Write FLX on top of the file.
            options.Stream.Write(FluxionMark, 0, FluxionMark.Length);

            // Write version
            options.Stream.WriteByte(root.Version);

            // Count the nodes, attributes and data first so we can use it for arrays below
            CountV3(root, out var nC, out var aC);

            // Create arrays and insertion points
            var dataArray = new List<object>();
            var list = new FluxionItem[aC + nC];
            var p = 0;

            // List all nodes, attributes and data to arrays.
            var rootNodeId = ListV3(root, options, ref list, ref dataArray, ref p);

            // Optimize it
            if (options.Optimize)
                list = OptimizeV3(options, list);

            // Write node count, attribute count and data count to stream
            WriteVarInt(options.Stream, nC + aC);
            WriteVarInt(options.Stream, dataArray.Count);

            // Write data to stream
            foreach (var data in dataArray)
                WriteValue_V3(data, options.Stream);


            // Write attributes and nodes to stream
            for (var i = 0; i < list.Length; i++)
            {
                options.OnProgressChanged(root, list.Length, i);
                var f3Item = list[i];
                byte type = 0;
                type += f3Item.IsReference ? (byte)1 : (byte)0;
                type += f3Item is FluxionAttribute ? (byte)2 : (byte)0;
                type += !string.IsNullOrWhiteSpace(f3Item.Name) ? (byte)4 : (byte)0;
                type += f3Item.Value != null ? (byte)8 : (byte)0;
                switch (f3Item)
                {
                    case FluxionAttribute _:
                        type += (byte)(f3Item.ValueType * 16);
                        break;
                    case FluxionNode f3Node:
                        type += f3Node.Count > 0 ? (byte)16 : (byte)0;
                        type += f3Node.CopyChildren ? (byte)32 : (byte)0;
                        type += f3Node.Attributes.Count > 0 ? (byte)64 : (byte)0;
                        type += f3Node.CopyAttributes ? (byte)128 : (byte)0;
                        break;
                }

                options.Stream.WriteByte(type);

                if (f3Item.IsReference)
                {
                    WriteVarInt(options.Stream, f3Item.ReferenceId);
                    WriteVarInt(options.Stream, f3Item.ReferenceCount);
                }

                if (!string.IsNullOrWhiteSpace(f3Item.Name) && f3Item.NameId >= 0)
                    WriteVarInt(options.Stream, f3Item.NameId);

                if (f3Item.Value != null && f3Item.ValueId >= 0)
                {
                    if (f3Item is FluxionNode) options.Stream.WriteByte(f3Item.ValueType);

                    WriteVarInt(options.Stream, f3Item.ValueId);
                }

                if (!(f3Item is FluxionNode node)) continue;
                if (node.Count > 0)
                {
                    var childrenIds = node.ChildrenIDs;
                    if (node.IsReference && list[f3Item.ReferenceId] is FluxionNode refNode)
                        childrenIds = node.ChildrenIDs.Except(refNode.ChildrenIDs).ToArray();

                    var incremental = true;

                    var lastItem = -1;
                    var smallestItem = int.MaxValue;
                    var biggestItem = -1;

                    foreach (var id in childrenIds)
                    {
                        if (id < smallestItem) smallestItem = id;

                        if (biggestItem < id) biggestItem = id;

                        if (lastItem < 0)
                        {
                            lastItem = id;
                            continue;
                        }

                        if (id - lastItem == 1) continue;
                        incremental = false;
                        break;
                    }

                    if (incremental)
                    {
                        options.Stream.WriteByte(1);
                        WriteVarInt(options.Stream, smallestItem);
                        WriteVarInt(options.Stream, biggestItem);
                    }
                    else
                    {
                        options.Stream.WriteByte(0);
                        WriteVarInt(options.Stream, node.Count);
                        foreach (var c in childrenIds)
                            WriteVarInt(options.Stream, c);
                    }
                }

                if (node.Attributes.Count <= 0) continue;
                var attrIds = node.AttributesIDs;
                if (node.IsReference && list[node.ReferenceId] is FluxionNode refNode1)
                    attrIds = node.AttributesIDs.Except(refNode1.AttributesIDs).ToArray();

                var attrIncremental = true;

                var lastAttrItem = -1;
                var smallestAttrItem = int.MaxValue;
                var biggestAttrItem = -1;

                foreach (var id in attrIds)
                {
                    if (id < smallestAttrItem) smallestAttrItem = id;

                    if (biggestAttrItem < id) biggestAttrItem = id;

                    if (lastAttrItem < 0)
                    {
                        lastAttrItem = id;
                        continue;
                    }

                    if (id - lastAttrItem == 1) continue;
                    attrIncremental = false;
                    break;
                }

                if (attrIncremental)
                {
                    options.Stream.WriteByte(1);
                    WriteVarInt(options.Stream, smallestAttrItem);
                    WriteVarInt(options.Stream, biggestAttrItem);
                }
                else
                {
                    options.Stream.WriteByte(0);
                    WriteVarInt(options.Stream, node.Attributes.Count);
                    foreach (var a in attrIds)
                        WriteVarInt(options.Stream, a);
                }
            }

            // Write the root node's ID to stream
            WriteVarInt(options.Stream, rootNodeId);
        }

        #endregion

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
                    listN1 &&
                listN1.Count <= 0 &&
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
                        if (adc.FindAll(it => it.IsHash && it.Data == hash) is List<AnalyzedDataContent> listH &&
                            listH.Count > 0)
                            break;
                        adc.Add(new AnalyzedDataContent(0, hash, true) { HashedArray = hash });
                        estimation += EstimateValueSize_V2(byteArray, encoding);
                    }

                    break;

                default:
                    if (adc.FindAll(it => it.Data == node.Value) is List<AnalyzedDataContent> list &&
                        list.Count > 0)
                        break;
                    if (ValueIsUnique(node.Value)) break;
                    adc.Add(new AnalyzedDataContent(0, node.Value));
                    estimation += EstimateValueSize_V2(node.Value, encoding);
                    break;
            }

            foreach (var attr in node.Attributes)
            {
                if (!string.IsNullOrWhiteSpace(attr.Name) &&
                    adc.FindAll(it => !it.IsHash && it.Data is string s && s == attr.Name) is List<AnalyzedDataContent>
                        listN &&
                    listN.Count <= 0 &&
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
                            if (adc.FindAll(it => it.IsHash && it.Data == hash) is List<AnalyzedDataContent> listH &&
                                listH.Count > 0)
                                break;
                            adc.Add(new AnalyzedDataContent(0, hash, true) { HashedArray = hash });
                            estimation += EstimateValueSize_V2(byteArray, encoding);
                        }

                        break;

                    default:
                        if (adc.FindAll(it => it.Data == attr.Value) is List<AnalyzedDataContent> list &&
                            list.Count > 0)
                            break;
                        if (ValueIsUnique(attr.Value)) break;
                        adc.Add(new AnalyzedDataContent(0, attr.Value));
                        estimation += EstimateValueSize_V2(attr.Value, encoding);
                        break;
                }
            }

            if (node.Count <= 0) return estimation;
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var subNode in node.Children) estimation += Estimate_V2(subNode, encoding, ref adc);

            return estimation;
        }

        private static void Write_V2(
            this FluxionNode node,
            FluxionWriteOptions options,
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
                options.Stream.Write(FluxionMark, 0, FluxionMark.Length);

                // Write version
                options.Stream.WriteByte(node.Version);

                // Write Encoding
                options.Stream.WriteByte(GetEncodingId(encoding));

                // Estimate Data Size
                var dataPos = new List<AnalyzedDataContent>();
                var dataSize = Estimate_V2(node, encoding, ref dataPos);
                var dataEndPos = options.Stream.Position + EstimateValueSize_V2(dataSize, encoding) + dataSize;
                WriteVarLong(options.Stream, dataEndPos);

                var dataEndPos2 = WriteData_V2(options.Stream, encoding, ref dataPos);
                if (dataEndPos2 != dataEndPos)
                    throw new FluxionEstimationError(dataEndPos, dataEndPos2);
                analyticsData = dataPos.ToArray();
            }

            // Get analyzed data for this node.
            AnalyzedDataContent nodeAd0 = null;
            AnalyzedDataContent nodeAn0 = null;
            switch (node.Value)
            {
                default:
                    var analyzedDataContents =
                        analyticsData.Where(it => it.Data == node.Value).ToArray();
                    nodeAd0 = analyzedDataContents[0] ?? throw new FluxionAnalyzedDataMissingException();
                    break;

                case null:
                case true:
                case false:
                    break;
            }

            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                var nodeAn =
                    analyticsData.Where(it => it.Data is string s && s == node.Name).ToArray();
                nodeAn0 = nodeAn[0] ?? throw new FluxionAnalyzedDataMissingException();
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

            if (ValueIsUnique(node.Value))
                valueType = (byte)(valueType ^ 128); // Unique flag

            // Write the type.
            options.Stream.WriteByte(valueType);

            // Node Children count (only if node has children).
            if (node.Count > 0) WriteVarInt(options.Stream, node.Count);

            // Node Name (only if it has one).
            if (!string.IsNullOrWhiteSpace(node.Name) && nodeAn0 != null)
                WriteVarLong(options.Stream, nodeAn0.Position);

            // Write data position (if not null, or bool)
            if (nodeAd0 != null)
                WriteVarLong(options.Stream, nodeAd0.Position);

            if (node.Attributes.Count > 0) WriteVarInt(options.Stream, node.Attributes.Count);

            // Same thing here.
            foreach (var attr in node.Attributes)
            {
                // Get analyzed data for this attribute.
                AnalyzedDataContent attrAn0 = null;
                AnalyzedDataContent attrAd0 = null;
                if (!string.IsNullOrWhiteSpace(attr.Name))
                {
                    var attrAn =
                        analyticsData.Where(it => it.Data is string s && s == attr.Name).ToArray();
                    attrAn0 = attrAn[0] ?? throw new FluxionAnalyzedDataMissingException();
                }

                switch (attr.Value)
                {
                    case null:
                    case true:
                    case false:
                        break;

                    default:
                        var attrAd = analyticsData.Where(it => it.Data == attr.Value).ToArray();
                        attrAd0 = attrAd[0] ?? throw new FluxionAnalyzedDataMissingException();
                        break;
                }

                // Get the value type
                var attrValueType = GetValueType_V2(attr.Value);

                // Check if the node has a name, XOR with 16 to set the flag.
                if (!string.IsNullOrWhiteSpace(attr.Name))
                    attrValueType = (byte)(attrValueType ^ 16);

                if (ValueIsUnique(attr.Value))
                    valueType = (byte)(valueType ^ 128); // Unique flag

                // Write the value type.
                options.Stream.WriteByte(attrValueType);

                // Check if attribute has name, if it has one then write position.
                if (!string.IsNullOrWhiteSpace(attr.Name) && attrAn0 != null)
                    WriteVarLong(options.Stream, attrAn0.Position);

                // Write the value position.
                if (attrAd0 != null)
                    WriteVarLong(options.Stream, attrAd0.Position);
            }

            // Recursion: Write other nodes (not as root node).
            foreach (var childNode in node.Children)
                Write_V2(childNode, options, encoding, false, analyticsData);
        }

        #endregion Fluxion 2

        #region Fluxion 1

        private static void Write_V1(
            this FluxionNode node,
            FluxionWriteOptions options,
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
                options.Stream.Write(FluxionMark, 0, FluxionMark.Length);

                // Write version
                options.Stream.WriteByte(node.Version);

                // Write Encoding
                options.Stream.WriteByte(GetEncodingId(encoding));
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
            options.Stream.WriteByte(valueType);

            // Node Children count (only if node has children).
            if (node.Count > 0) WriteVarInt(options.Stream, node.Count);

            // Node Name (only if it has one), encoding first then length then name.
            if (!string.IsNullOrWhiteSpace(node.Name))
                WriteByteArrWithVarInt(options.Stream, encoding.GetBytes(node.Name));

            switch (node.Value)
            {
                // Check if the value is string, or byte array for variable-length encoding.
                case string stringValue:
                    WriteByteArrWithVarInt(options.Stream, encoding.GetBytes(stringValue));
                    break;
                case byte[] _:
                    WriteByteArrWithVarInt(options.Stream, value);
                    break;
                default:
                    options.Stream.Write(value, 0, value.Length);
                    break;
            }

            if (node.Attributes.Count > 0) WriteVarInt(options.Stream, node.Attributes.Count);

            // Same thing here.
            foreach (var attr in node.Attributes)
            {
                // Get value type.
                var attrValueType = GetValueType(attr.Value, out var attrValue);

                // Check if the node has a name, XOR with 16 to set the flag.
                if (!string.IsNullOrWhiteSpace(attr.Name))
                    attrValueType = (byte)(attrValueType ^ 16);

                // Write the value.
                options.Stream.WriteByte(attrValueType);

                // Check if attribute has name, if it has one then write encoding, length and the name.
                if (!string.IsNullOrWhiteSpace(attr.Name))
                    WriteByteArrWithVarInt(options.Stream, encoding.GetBytes(attr.Name));

                switch (attr.Value)
                {
                    // Check if the value is string, or byte array for variable-length encoding.
                    case string attrString:
                        WriteByteArrWithVarInt(options.Stream, encoding.GetBytes(attrString));
                        break;
                    case byte[] attByteArray:
                        WriteByteArrWithVarInt(options.Stream, attByteArray);
                        break;
                    default:
                    {
                        if (attrValue.Length > 0) // Only write if value is not null, bool, etc.
                            options.Stream.Write(attrValue, 0, attrValue.Length);
                        break;
                    }
                }
            }

            // Recursion: Write other nodes (not as root node).
            foreach (var childNode in node.Children)
                Write_V1(childNode, options, encoding, false);
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

        private static object ReadBytesFromType_V3(Stream stream, int valueType)
        {
            var uniqueFlag = IsBitSet((byte)valueType, 7);
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
                    var byteValue = stream.ReadByte();
                    return (byte)byteValue;
                case 4:
                    if (uniqueFlag) return (sbyte)0;
                    var sbyteValue = stream.ReadByte();
                    return (sbyte)sbyteValue;
                case 5:
                    if (uniqueFlag) return char.MinValue;
                    var charValue = char.MinValue;
                    var charShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        charValue |= (char)((b & 0x7F) << charShift);
                        charShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    return charValue;

                case 6:
                    short shortValue = 0;
                    var shortShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        shortValue |= (short)((b & 0x7F) << shortShift);
                        shortShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    return uniqueFlag ? (short)-shortValue : shortValue;
                case 7:
                    if (uniqueFlag) return (ushort)0;
                    ushort ushortValue = 0;
                    var ushortShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        ushortValue |= (ushort)((b & 0x7F) << ushortShift);
                        ushortShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    return ushortValue;

                case 8:
                    var intValue = 0;
                    var intShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        intValue |= (b & 0x7F) << intShift;
                        intShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    return uniqueFlag ? -intValue : intValue;
                case 9:
                    if (uniqueFlag) return (uint)0;
                    uint uintValue = 0;
                    var uintShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        uintValue |= (uint)(b & 0x7F) << uintShift;
                        uintShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    return uintValue;

                case 10:
                    long longValue = 0;
                    var longShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        longValue |= (long)(b & 0x7F) << longShift;
                        longShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    return uniqueFlag ? -longValue : longValue;
                case 11:
                    if (uniqueFlag) return (ulong)0;
                    ulong ulongValue = 0;
                    var ulongShift = 0;

                    while (true)
                    {
                        var b = (byte)stream.ReadByte();
                        ulongValue |= (ulong)(b & 0x7F) << ulongShift;
                        ulongShift += 7;

                        if ((b & 0x80) == 0) break;
                    }

                    return ulongValue;

                case 12:
                    if (uniqueFlag) return 0F;
                    var floatValue = new byte[sizeof(float)];
                    var floatRead = stream.Read(floatValue, 0, floatValue.Length);
                    if (floatRead != sizeof(float))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToSingle(floatValue, 0);
                case 13:
                    if (uniqueFlag) return 0D;
                    var doubleValue = new byte[sizeof(double)];
                    var doubleRead = stream.Read(doubleValue, 0, doubleValue.Length);
                    if (doubleRead != sizeof(double))
                        throw new FluxionEndOfStreamException();
                    return BitConverter.ToDouble(doubleValue, 0);

                case 14:
                    if (uniqueFlag) return string.Empty;
                    var stringValue = Encoding.UTF8.GetString(DecodeByteArrWithVarInt(stream));
                    return stringValue;

                case 15:
                    if (uniqueFlag) return Array.Empty<byte>();
                    var byteArrayValue = DecodeByteArrWithVarInt(stream);
                    return byteArrayValue;

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

        private static bool IsBitSet(byte value, int bitPosition)
        {
            if (bitPosition < 0 || bitPosition > 7)
                throw new ArgumentOutOfRangeException(
                    nameof(bitPosition),
                    "Must be between 0 and 7"
                );
            var bitmask = 1 << bitPosition;
            return (value & bitmask) != 0;
        }

        private static bool IsBitSet(int value, int bitPosition)
        {
            if (bitPosition < 0 || bitPosition > (sizeof(int) - 1) * 8)
                throw new ArgumentOutOfRangeException(
                    nameof(bitPosition),
                    $"Must be between 0 and {(sizeof(int) - 1) * 8}"
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
            long value = 0;
            var shift = 0;
            byte b;
            do
            {
                b = (byte)stream.ReadByte();
                value |= (uint)((b & 0x7F) << shift);
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

        private static void WriteValue_V3(object input, Stream stream)
        {
            stream.WriteByte(GetValueType_V2(input));
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
                    WriteByteArrWithVarInt(stream, Encoding.UTF8.GetBytes(stringValue));
                    break;

                case byte[] byteArrValue:
                    WriteByteArrWithVarInt(stream, byteArrValue);
                    break;

                default:
                    throw new FluxionValueTypeException(input.GetType().FullName);
            }
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
                    WriteByteArrWithVarInt(stream, byteArrValue);
                    break;

                default:
                    throw new FluxionValueTypeException(input.GetType().FullName);
            }
        }

        #endregion Helpers

        #region Encodings

        private static byte GetEncodingId(this Encoding encoding)
        {
            switch (encoding)
            {
                case UTF8Encoding _:
                    return 0;
                case UnicodeEncoding _:
                    return 1;
                case UTF32Encoding _:
                    return 2;
                default:
                    throw new FluxionEncodingException(encoding);
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

    #region Read/Write Options

    /// <summary>
    /// Interface for common things inside of Read/Write options classes.
    /// </summary>
    public class FluxionReadWrite
    {
        /// <summary>
        /// Event arguments for progress changed while reading/writing.
        /// </summary>
        public class FluxionProgressChangedEventArgs : EventArgs
        {
            /// <summary>
            /// Creates a new <see cref="FluxionProgressChangedEventArgs"/>.
            /// </summary>
            /// <param name="total">Total nodes count.</param>
            /// <param name="current">Current nodes count.</param>
            public FluxionProgressChangedEventArgs(int total, int current)
            {
                Total = total;
                Current = current;
            }

            /// <summary>
            /// Total nodes count.
            /// </summary>
            // ReSharper disable once UnusedAutoPropertyAccessor.Global
            public int Total { get; internal set; }

            /// <summary>
            /// Current nodes count.
            /// </summary>
            // ReSharper disable once UnusedAutoPropertyAccessor.Global
            public int Current { get; internal set; }
        }

        public event EventHandler<FluxionProgressChangedEventArgs> ProgressChanged;

        internal void OnProgressChanged(object sender, FluxionProgressChangedEventArgs e)
        {
            ProgressChanged?.Invoke(sender, e);
        }

        internal void OnProgressChanged(object sender, int total, int current)
        {
            ProgressChanged?.Invoke(sender, new FluxionProgressChangedEventArgs(total, current));
        }
    }

    /// <summary>
    /// Options for reading a Fluxion formatted file or stream.
    /// </summary>
    public class FluxionReadOptions : FluxionReadWrite
    {
        /// <summary>
        /// Stream to read from.
        /// </summary>
        public Stream Stream { get; set; }

        /// <summary>
        /// File to read from.
        /// </summary>
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        public string File { get; set; }

        /// <summary>
        /// <inheritdoc cref="System.IO.FileMode"/>
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public FileMode FileMode { get; set; } = FileMode.Create;

        /// <summary>
        /// <inheritdoc cref="System.IO.FileAccess"/>
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public FileAccess FileAccess { get; set; } = FileAccess.Write;

        /// <summary>
        /// <inheritdoc cref="System.IO.FileShare"/>
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public FileShare FileShare { get; set; } = FileShare.ReadWrite;
    }

    /// <summary>
    /// Options for writing a Fluxion node as root to a file or stream.
    /// </summary>
    public class FluxionWriteOptions : FluxionReadWrite
    {
        /// <summary>
        /// Stream to write on.
        /// </summary>
        public Stream Stream { get; set; }

        /// <summary>
        /// File to write on.
        /// </summary>
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        public string File { get; set; }

        /// <summary>
        /// <inheritdoc cref="System.IO.FileMode"/>
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public FileMode FileMode { get; set; } = FileMode.Create;

        /// <summary>
        /// <inheritdoc cref="System.IO.FileAccess"/>
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public FileAccess FileAccess { get; set; } = FileAccess.Write;

        /// <summary>
        /// <inheritdoc cref="System.IO.FileShare"/>
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public FileShare FileShare { get; set; } = FileShare.ReadWrite;

        /// <summary>
        /// Determines the tolerance for float values.
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public float FloatTolerance { get; set; } = 0.001F;

        /// <summary>
        /// Determines the tolerance for double values.
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public double DoubleTolerance { get; set; } = 0.001D;

        /// <summary>
        /// Specifies the encoding to be used. Only effective on v1 and v2.
        /// <list type="bullet|value">
        /// <listheader>
        ///  Fluxion supports these encodings:
        /// </listheader>
        /// <item><see cref="System.Text.Encoding.UTF8"/></item>
        /// <item><see cref="System.Text.Encoding.Unicode"/></item>
        /// <item><see cref="System.Text.Encoding.UTF32"/></item>
        /// </list>
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public Encoding Encoding { get; set; } = Encoding.UTF8;

        /// <summary>
        /// Uses Reference nodes/attributes to make the file size smaller on repeating nodes. Only effective in v3.
        /// </summary>
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public bool Optimize { get; set; } = true;

        /// <summary>
        /// Specifies the Fluxion version.
        /// </summary>
        public byte Version { get; set; } = Fluxion.Version;
    }

    #endregion Read/Write Options

    public abstract class FluxionItem
    {
        internal bool CopyChildren;
        internal bool CopyAttributes;
        internal int NameId;
        internal int ValueId;
        internal byte ValueType;
        internal int[] ChildrenIDs;
        internal int[] AttributesIDs;
        internal bool IsReference;
        internal int ReferenceId = -1;
        internal int ReferenceCount = 1;

        internal abstract FluxionItem FullClone();

        internal abstract bool IsDeepEqual(FluxionItem other, float floatTolerance = 0.001F,
            double doubleTolerance = 0.001D);

        /// <summary>
        /// Name of this node/attribute.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Value of this node/attribute.  Currently, these types are supported:
        ///     <para />
        ///     <c>null</c>, <c>true</c>, <c>false</c>, <see cref="byte" />, <see cref="sbyte" />, <see cref="char" />,
        ///     <see cref="short" />, <see cref="ushort" />, <see cref="int" />, <see cref="uint" />, <see cref="long" />,
        ///     <see cref="ulong" />, <see cref="float" />, <see cref="double" />, <see cref="string" />, <c>byte[]</c>.
        /// </summary>
        public object Value { get; set; }
    }

    #region Node

    /// <summary>
    ///     A Fluxion node class.
    /// </summary>
    public class FluxionNode : FluxionItem
    {
        private List<FluxionNode> _children = new List<FluxionNode>();
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

        internal override bool IsDeepEqual(FluxionItem other, float floatTolerance = 0.001F,
            double doubleTolerance = 0.001D)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;

            if (Name != other.Name || !Fluxion.IsEqual(Value, other.Value, floatTolerance, doubleTolerance))
                return false;

            if (!(other is FluxionNode node)) return false;
            if (Children.Count != node.Count)
                return false;
            CopyChildren = AreChildrenSubset(Children, node.Children);
            if (Attributes.Count != node.Attributes.Count)
                return false;
            CopyAttributes = AreAttributesSubset(Attributes, node.Attributes);
            return true;
        }

        private static bool AreChildrenSubset(List<FluxionNode> children1, List<FluxionNode> children2,
            float floatTolerance = 0.001F,
            double doubleTolerance = 0.001D)
        {
            return children1.All(child1 =>
                children2.Any(child2 => child1.IsDeepEqual(child2, floatTolerance, doubleTolerance)));
        }

        private static bool AreAttributesSubset(List<FluxionAttribute> attributes1, List<FluxionAttribute> attributes2,
            float floatTolerance = 0.001F,
            double doubleTolerance = 0.001D)
        {
            return attributes1.All(attribute1 =>
                attributes2.Any(attribute2 => attribute1.IsDeepEqual(attribute2, floatTolerance, doubleTolerance)));
        }


        /// <summary>
        ///     Parent of this node.
        /// </summary>
        public FluxionNode Parent { get; internal set; }

        /// <summary>
        ///     Collection of the children in this node.
        /// </summary>
        // ReSharper disable once MemberCanBePrivate.Global
        public List<FluxionNode> Children
        {
            get => _children;
            set
            {
                foreach (var child in value) child.Parent = this;

                _children = value;
            }
        }

        /// <summary>
        ///     Attributes of this node.
        /// </summary>
        public List<FluxionAttribute> Attributes { get; set; } = new List<FluxionAttribute>();

        /// <summary>
        ///     Gets a children from index.
        /// </summary>
        /// <param name="index">Index of the children.</param>

        // ReSharper disable once UnusedMember.Global
        // ReSharper disable once MemberCanBePrivate.Global
        public FluxionNode this[int index] => _children[index];

        /// <summary>
        ///     Gets the total amount of child nodes of this node.
        /// </summary>
        public int Count => _children.Count;

        /// <summary>
        ///     Clones this node.
        /// </summary>
        /// <param name="copyName">Copy name to the clone.</param>
        /// <param name="copyValue">Copy the value to the clone.</param>
        /// <param name="copyAttributes">Copies all attributes to clone.</param>
        /// <param name="copyChildren">Copies all child nodes to clone.</param>
        /// <returns>Exact copy of this node.</returns>
        public FluxionNode Clone(bool copyName, bool copyValue, bool copyAttributes, bool copyChildren)
        {
            var newNode = new FluxionNode { Name = copyName ? Name : string.Empty, Value = copyValue ? Value : null };

            if (copyAttributes && Attributes.Count > 0)
                for (var i = 0; i < Attributes.Count; i++)
                    newNode.Attributes.Add(Attributes[i].Clone(copyName, copyValue));

            if (!copyChildren || Count <= 0) return newNode;

            for (var i = 0; i < Count; i++)
                newNode.Add(this[i].Clone(copyName, copyValue, copyAttributes, true));

            return newNode;
        }

        /// <summary>
        ///     Gets the index of a node.
        /// </summary>
        /// <param name="node">Node to get the index of.</param>
        /// <returns>Index of <paramref name="node" />.</returns>
        // ReSharper disable once UnusedMember.Global
        public int IndexOf(FluxionNode node)
        {
            if (node == null) return -1;
            for (var i = 0; i < _children.Count; i++)
                if (node == _children[i])
                    return i;

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

        private static bool CheckIfNodeIsInTree(FluxionNode node, FluxionNode newParent)
        {
            return node.Count > 0
                   && (
                       node.Contains(newParent)
                       || node.Children.Any(children => CheckIfNodeIsInTree(children, newParent))
                   );
        }

        #endregion Internal Code

        internal override FluxionItem FullClone()
        {
            return Clone(true, true, true, true);
        }
    }

    #endregion Node

    #region Attributes

    /// <summary>
    ///     A Fluxion Node Attribute Class.
    /// </summary>
    public class FluxionAttribute : FluxionItem
    {
        /// <summary>
        ///     Clones an attribute.
        /// </summary>
        /// <param name="copyName">Determines if the name should be copied also.</param>
        /// <param name="copyValue">Determines if value should be copied also.</param>
        /// <returns>Exact copy of this attribute.</returns>
        public FluxionAttribute Clone(bool copyName, bool copyValue)
        {
            return new FluxionAttribute { Name = copyName ? Name : string.Empty, Value = copyValue ? Value : null };
        }

        internal override FluxionItem FullClone()
        {
            return Clone(true, true);
        }

        internal override bool IsDeepEqual(FluxionItem other, float floatTolerance = 0.001F,
            double doubleTolerance = 0.001D)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;

            // Compare basic properties
            return Name == other.Name && Fluxion.IsEqual(Value, other.Value, floatTolerance, doubleTolerance);
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

        public class FluxionUnexpectedItemTypeException : FluxionException
        {
            internal FluxionUnexpectedItemTypeException(int blockId, string type) : base(
                $"Item type \"{blockId}\" is not {type}. This can occur if the file is corrupted or something went wrong while reading it.")
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
        ///     Exception to throw when something went wrong with the Fluxion file and reading is dis-oriented.
        /// </summary>
        public class FluxionDisorientedReadException : FluxionException
        {
            /// <summary>
            ///     Throws an exception where something went wrong with the Fluxion file and reading is dis-oriented.
            /// </summary>
            internal FluxionDisorientedReadException(int value) : base(
                $"Disoriented reading (read \"{value:X}\"). File might be corrupted.")
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
        ///  Exception to throw if the read value type is not matching with the actual value type of value. 
        /// </summary>
        public class FluxionValueTypeMismatchException : FluxionException
        {
            /// <summary>
            /// Throws an exception if the read value type is not matching with the actual value type of value. 
            /// </summary>
            /// <param name="expected">The expected value.</param>
            /// <param name="result">Read value.</param>
            internal FluxionValueTypeMismatchException(int expected, int result) : base(
                $"Value type mismatch. Expected {expected}, got {result}")
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