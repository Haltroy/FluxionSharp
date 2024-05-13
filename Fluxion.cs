using System;
using System.Collections;
using System.IO;
using System.Linq;
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
        public const byte Version = 1;

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

            // Version check
            if (versionByte > Version)
                throw new FluxionUnsupportedVersionException((byte)versionByte);
            root.Version = (byte)versionByte;

            root = ReadRecurse(stream, encoding, root, true);

            return root;
        }

        private static FluxionNode ReadRecurse(
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

            if (!noChild)
                for (var i = 0; i < childrenCount; i++)
                    node.Add(ReadRecurse(stream, encoding, node));

            return node;
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

        #endregion Read

        #region Write

        /// <summary>
        ///     Writes a Fluxion node (turns it to Root node in the process) to a stream.
        /// </summary>
        /// <param name="node">Node to write.</param>
        /// <param name="stream">Stream to write.</param>
        /// <param name="encoding">Encodings of the string values and names.</param>
        /// <param name="asRoot">
        ///     Please don't use this, this is used only internally by Fluxion by itself. Let this keep on
        ///     <c>true</c>. This writes header.
        /// </param>
        public static void Write(
            this FluxionNode node,
            Stream stream,
            Encoding encoding,
            bool asRoot = true
        )
        {
            encoding = encoding ?? Encoding.Default;
            // Header code that only should be run on Root node.
            if (asRoot)
            {
                // Set node to root.
                node.IsRoot = true;

                // Write FLX to top of the file.
                stream.WriteByte(0x46);
                stream.WriteByte(0x4C);
                stream.WriteByte(0x58);

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
                Write(child_node, stream, encoding, false);
        }

        /// <summary>
        ///     Writes a Fluxion node to a file.
        /// </summary>
        /// <param name="node">Node to write.</param>
        /// <param name="fileName">Path of the file.</param>
        /// <param name="encoding">Determines the encoding of the string values and names.</param>
        /// <param name="fileShare">Determines if other processes can access the file while writing to it.</param>
        // ReSharper disable once UnusedMember.Global
        public static void Write(
            this FluxionNode node,
            string fileName,
            Encoding encoding,
            FileShare fileShare = FileShare.ReadWrite
        )
        {
            encoding = encoding ?? Encoding.Default;
            using (
                var stream = File.Exists(fileName)
                    ? new FileStream(fileName, FileMode.Truncate, FileAccess.ReadWrite, fileShare)
                    : File.Create(fileName)
            )
            {
                Write(node, stream, encoding);
            }
        }

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

        private static bool isBitSet(byte value, int bitPosition)
        {
            if (bitPosition < 0 || bitPosition > 7)
                throw new ArgumentOutOfRangeException(
                    nameof(bitPosition),
                    "Must be between 0 and 7"
                );

            // Create a bitmask with the target bit set to 1
            var bitmask = 1 << bitPosition;

            // Check if the bit is set using bitwise AND
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
                value >>= 7; // Shift remaining value by 7 bits
                b |= (byte)(value > 0 ? 0x80 : 0); // Set continuation bit if more bytes needed
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
                value |= (b & 0x7F) << shift; // Extract data bits and apply shift
                shift += 7;
            } while ((b & 0x80) != 0); // Check for continuation bit

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

    /// <summary>
    ///     A Fluxion node class.
    /// </summary>
    public class FluxionNode : CollectionBase
    {
        private byte _version = Fluxion.Version;

        /// <summary>
        ///     Determines if a node is root node or not.
        /// </summary>
        public bool IsRoot { get; internal set; }

        /// <summary>
        ///     Parent of this node.
        /// </summary>
        public FluxionNode Parent { get; internal set; }

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

        /// <summary>
        ///     Collection of the children in this node.
        /// </summary>
        // ReSharper disable once MemberCanBePrivate.Global
        public FluxionNode[] Children

        {
            get
            {
                var children = new FluxionNode[List.Count];
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
        public FluxionNode this[int index] => (FluxionNode)List[index];

        /// <summary>
        ///     Gets a children from name.
        /// </summary>
        /// <param name="name">Name of the children.</param>

        // ReSharper disable once UnusedMember.Global
        public FluxionNode this[string name]
        {
            get
            {
                foreach (var t in List)
                {
                    var node = (FluxionNode)t;
                    if (string.Equals(node.Name, name))
                        return node;
                }

                return null;
            }
        }

        /// <summary>
        ///     Gets the index of a node.
        /// </summary>
        /// <param name="node">Node to get the index of.</param>
        /// <returns>Index of <paramref name="node" />.</returns>
        // ReSharper disable once UnusedMember.Global
        public int IndexOf(FluxionNode node)
        {
            if (node != null)
                return List.IndexOf(node);
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
            if (node != null)
            {
                if (Parent == node || CheckIfNodeIsInTree(node, Parent))
                    throw new FluxionParentException();
                node.Parent?.Remove(node);
                node.Parent = Parent;
                return List.Add(node);
            }

            return -1;
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
            InnerList.Remove(node);
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

            InnerList.AddRange(nodes);
        }

        /// <summary>
        ///     Inserts a node into a specific index.
        /// </summary>
        /// <param name="index">Index to insert.</param>
        /// <param name="node">Node to insert.</param>
        // ReSharper disable once UnusedMember.Global
        public void Insert(int index, FluxionNode node)
        {
            if (index > List.Count || node == null)
                return;
            if (Parent == node || CheckIfNodeIsInTree(node, Parent))
                throw new FluxionParentException();
            node.Parent?.Remove(node);
            node.Parent = Parent;
            List.Insert(index, node);
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
            return List.Contains(node);
        }

        #region Internal Code

        private bool CheckIfNodeIsInTree(FluxionNode node, FluxionNode new_parent)
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
    public class FluxionAttribute
    {
        /// <summary>
        ///     Name of the attribute.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     Value of this attribute. Currently, these types are supported:
        ///     <para />
        ///     <c>null</c>, <c>true</c>, <c>false</c>, <see cref="byte" />, <see cref="sbyte" />, <see cref="char" />,
        ///     <see cref="short" />, <see cref="ushort" />, <see cref="int" />, <see cref="uint" />, <see cref="long" />,
        ///     <see cref="ulong" />, <see cref="float" />, <see cref="double" />, <see cref="string" />, <c>byte[]</c>.
        /// </summary>
        public object Value { get; set; }
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
            ///     Thrıows an exception telling a node is made for a Fluxion version that isn't suported by this library (ex. future
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
    }

    #endregion Exceptions
}