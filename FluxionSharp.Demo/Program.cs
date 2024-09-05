/*
 In this example, we are going to do the following (in order):
     1) Create our information.
     2) Store that information to a MemoryStream and read it back from it. Check if the information we saved are same.
     3) Compare sizes with other nodal formats.
*/

// SECTION 0 - INITIAL SETUP

// Let's set up an example application. First, we add the required usings to the top of our files.

using System.Text;
using FluxionSharp;

//  Let's set up our namespace as well.
namespace FluxionSharpDemo;

// Let's set up our C# program.
public static class Program
{
    // Usually, many developers put "string[] args" to get command-line arguments but since we don't use it in our program we can safely remove it.
    [STAThread]
    public static void Main()
    {
        // For our examples, let's use this encoding instead. You can change this to any encoding you want.
        var encoding = Encoding.UTF8;

        #region Section 1 - Create Information

        // Let's create a node that we will treat like a root node.
        // (Just like "<root>" on XML).
        var rootNode = new FluxionNode
        {
            Name = "MyRootNode", // We can put names to the root node.
            Value = null // Fluxion node values can be null.
        };

        // Let's add some information to the root node.

        // Let's make a user node and add it to our root node.
        var node1 = new FluxionNode { Name = "User", Value = "mike" };

        // Let's add some information to our user.
        node1.Attributes.Add(new FluxionAttribute { Name = "Age", Value = 35 });

        // And finally, let's add the user node we created to our root node.
        rootNode.Add(node1);

        // Let's create another user under our first user.

        var node1_1 = new FluxionNode { Name = "User", Value = "jeremy" };

        node1_1.Attributes.Add(new FluxionAttribute { Name = "Age", Value = 10 });

        node1.Add(node1_1);

        #endregion Section 1 - Create Information

        #region Section 2 - Store the Information

        // We need to tell the programming language that we are expecting a Fluxion node, so we need to define it here.
        FluxionNode rootNode_Read;

        // We also need to define the size here, so we can use it in Section 3.
        long fluxion_size;

        // Now we have some information to store into a place.
        // For that, we are going to store it in memory.
        // Usually, you can use it to save to a file with FileStream.
        // or compress and/or encrypt it with many stream classes
        // found on System.IO.Compression, System.Security.Cryptography
        // or a custom library.

        using (var stream = new MemoryStream())
        {
            // We can tell Fluxion to write to our stream.

            // For our example, let's use the Fluxion 1.

            // with: Fluxion.Write(rootNode, stream1, 1);
            // or

            rootNode.Write(stream, encoding, 1);

            // Save the size of our Fluxion file.
            fluxion_size = stream.Length;

            // Rewind the stream to the start.
            stream.Seek(0, SeekOrigin.Begin);

            // And we can start reading it again.
            rootNode_Read = Fluxion.Read(stream);
        }

        // Let's check if each of our nodes are read correct.

        if (rootNode.Name == rootNode_Read.Name && rootNode.Value == rootNode_Read.Value)
            Console.WriteLine("Root Node names and values are same!");
        else
            Console.WriteLine(
                $"Root Nodes are not same, {Environment.NewLine}"
                + $"    Name: {rootNode.Name} -> {rootNode_Read.Name} {Environment.NewLine}"
                + $"    Value: {rootNode.Value} -> {rootNode_Read.Value}"
            );

        // And let's do that to each of our node. But we should check if that child node exists before doing it.
        if (rootNode_Read.Count <= 0)
        {
            Console.WriteLine(
                "The child nodes are gone. Something bad happened to our Fluxion nodes."
            );
        }
        else
        {
            var node1_read = rootNode_Read[0];
            if (Equals(node1.Name, node1_read.Name) && Equals(node1.Value, node1_read.Value))
                Console.WriteLine("The first nodes' names and values are same!");
            else
                Console.WriteLine(
                    $"The first nodes are not same, {Environment.NewLine}"
                    + $"    Name: {node1.Name} -> {node1_read.Name} {Environment.NewLine}"
                    + $"    Value: {node1.Value} -> {node1_read.Value}"
                );

            // Let's check the attributes as well.
            for (var attr_i = 0; attr_i < node1.Attributes.Count; attr_i++)
                if (node1.Attributes[attr_i] is { } attr1)
                {
                    if (node1_read.Attributes[attr1.Name] is { } attr2)
                        Console.WriteLine(
                            Equals(attr1.Value, attr2.Value)
                                ? $"Attribute \"{attr1.Name}\" is same in both attributes."
                                : $"Attribute \"{attr1.Name}\" changed. {attr1.Value} -> {attr2.Value}"
                        );
                    else
                        Console.WriteLine(
                            $"Attribute \"{attr1.Name}\" does not exists in the other node. Something wrong happened to our Fluxion nodes."
                        );
                }

            if (node1_read.Count <= 0)
            {
                Console.WriteLine(
                    "This child nodes of our first node are gone. Something bad happened to our Fluxion nodes."
                );
            }
            else
            {
                var node1_1_read = node1_read[0];
                if (
                    Equals(node1_1.Name, node1_1_read.Name)
                    && Equals(node1_1.Value, node1_1_read.Value)
                )
                    Console.WriteLine("The first nodes' names and values are same!");
                else
                    Console.WriteLine(
                        $"The first nodes are not same, {Environment.NewLine}"
                        + $"    Name: {node1_1.Name} -> {node1_1_read.Name} {Environment.NewLine}"
                        + $"    Value: {node1_1.Value} -> {node1_1_read.Value}"
                    );
                for (var attr_i = 0; attr_i < node1_1.Attributes.Count; attr_i++)
                    if (node1_1.Attributes[attr_i] is { } attr1)
                    {
                        if (node1_1_read.Attributes[attr1.Name] is { } attr2)
                            Console.WriteLine(
                                Equals(attr1.Value, attr2.Value)
                                    ? $"Attribute \"{attr1.Name}\" is same in both attributes."
                                    : $"Attribute \"{attr1.Name}\" changed. {attr1.Value} -> {attr2.Value}"
                            );
                        else
                            Console.WriteLine(
                                $"Attribute \"{attr1.Name}\" does not exists in the other node. Something wrong happened to our Fluxion nodes."
                            );
                    }
            }
        }

        #endregion Section 2 - Store the Information

        #region Section 3 - Comparison with other nodal languages

        // In here, we are going to compare Fluxion with XML, YML and JSON on size.
        // In Section 2, we already got the size of a Fluxion file inside the using().

        // The XML equivalent of our root node:
        var xml =
            $"<?xml version=\"1.0\" encoding=\"{encoding.WebName}\" ?>"
            + "<root>"
            + "<User Value=\"mike\" Age=\"35\">"
            + "<User Value=\"jeremy\" Age=\"10\" />"
            + "</User>"
            + "</root>";

        // Notice how we can't include the name as inside the node because other child nodes has to be there instead.

        // Let's not save this into a file, we only need to get the approximated size,
        // so we can use the C# encodings to get the bytes that would represent this.
        var xmlSize = encoding.GetBytes(xml).Length;

        // Let's do the same thing with JSON and YML.
        const string json =
            "{"
            + "\"user\":{"
            + "\"name\":\"mike\","
            + "\"age\":35,"
            + "\"children\":["
            + "{\"user\":{"
            + "\"name\":\"jeremy\","
            + "\"age\":10"
            + "}"
            + "}"
            + "]"
            + "}"
            + "}";

        // Same thing happens here as well.

        var jsonSize = encoding.GetBytes(json).Length;

        var yml =
            $"user: {Environment.NewLine}"
            + $"  name: mike {Environment.NewLine}"
            + $"  age: 35 {Environment.NewLine}"
            + $"  children: {Environment.NewLine}"
            + $"  - user: {Environment.NewLine}"
            + $"      name: jeremy {Environment.NewLine}"
            + $"      age: 10 {Environment.NewLine}";

        var ymlSize = encoding.GetBytes(yml).Length;

        // Let's encode it with a newer (or current) Fluxion version.
        long flx2size;
        using (var stream = new MemoryStream())
        {
            rootNode.Write(stream, encoding);
            flx2size = stream.Length;
        }

        // Finally, let's print them all out to the console.

        Console.WriteLine(
            $"Sizes (in bytes) {Environment.NewLine}"
            + $"  - Fluxion (v1): {fluxion_size} {Environment.NewLine}"
            + $"  - Fluxion (v{Fluxion.Version}): {flx2size} {Environment.NewLine}"
            + $"  - XML: {xmlSize} {Environment.NewLine}"
            + $"  - JSON: {jsonSize} {Environment.NewLine}"
            + $"  - YML: {ymlSize} {Environment.NewLine}"
        );

        #endregion Section 3 - Comparison with other nodal languages
    }
}
