using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using System.Xml.Linq;

namespace TestGrainInterfaces
{
    /// <summary>
    /// The grain interface for the chat grain.
    /// </summary>
    [Alias("TestGrainInterfaces.IChatGrain")]
    public interface IChatGrain : IGrainWithStringKey
    {
        /// <summary> Return the current content of the chat room. </summary>
        [Alias("GetChat")]
        Task<XDocument> GetChat();

        /// <summary> Add a new post. </summary>
        [Alias("Post")]
        Task Post(Guid guid, string user, string text);

        /// <summary> Delete a specific post. </summary>
        [Alias("Delete")]
        Task Delete(Guid guid);

        /// <summary> Edit a specific post. </summary>
        [Alias("Edit")]
        Task Edit(Guid guid, string text);
    }

    /// <summary>
    /// Since XDocument does not seem to serialize automatically, we provide the necessary methods
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public class XDocumentSerialization : GeneralizedReferenceTypeSurrogateCodec<XDocument, XDocumentSurrogate>, IDeepCopier<XDocument>
    {
        public XDocumentSerialization(IValueSerializer<XDocumentSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override XDocument ConvertFromSurrogate(ref XDocumentSurrogate surrogate) => XDocument.Load(new StringReader(surrogate.Value));
        public override void ConvertToSurrogate(XDocument value, ref XDocumentSurrogate surrogate) => surrogate.Value = value.ToString();
        public XDocument DeepCopy(XDocument input, CopyContext context) => new(input);
    }

    [GenerateSerializer]
    [Alias("TestGrainInterfaces.XDocumentSurrogate")]
    public struct XDocumentSurrogate
    {
        [Id(0)]
        public string Value { get; set; }
    }
}
