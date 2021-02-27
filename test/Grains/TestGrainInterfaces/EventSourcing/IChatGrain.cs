using Orleans;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Hagar;
using Hagar.Codecs;
using Hagar.Cloning;
using Hagar.Serializers;

namespace TestGrainInterfaces
{
    /// <summary>
    /// The grain interface for the chat grain.
    /// </summary>
    public interface IChatGrain : IGrainWithStringKey
    {
        /// <summary> Return the current content of the chat room. </summary>
        Task<XDocument> GetChat();

        /// <summary> Add a new post. </summary>
        Task Post(Guid guid, string user, string text);

        /// <summary> Delete a specific post. </summary>
        Task Delete(Guid guid);

        /// <summary> Edit a specific post. </summary>
        Task Edit(Guid guid, string text);
    }

    /// <summary>
    /// Since XDocument does not seem to serialize automatically, we provide the necessary methods
    /// </summary>
    [Hagar.RegisterSerializer]
    [Hagar.RegisterCopier]
    public class XDocumentSerialization : GeneralizedReferenceTypeSurrogateCodec<XDocument, XDocumentSurrogate>, IDeepCopier<XDocument>
    {
        public XDocumentSerialization(IValueSerializer<XDocumentSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override XDocument ConvertFromSurrogate(ref XDocumentSurrogate surrogate) => XDocument.Load(new StringReader(surrogate.Value));
        public override void ConvertToSurrogate(XDocument value, ref XDocumentSurrogate surrogate) => surrogate.Value = value.ToString();
        public XDocument DeepCopy(XDocument input, CopyContext context) => new(input);
    }

    [Hagar.GenerateSerializer]
    public struct XDocumentSurrogate
    {
        [Id(0)]
        public string Value { get; set; }
    }
}
