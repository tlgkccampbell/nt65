using System.Runtime.Serialization;

namespace Norristown.Server.Protocol
{
    [DataContract]
    public class InitializeResult
    {
        [DataMember(Name = "capabilities")]
        public required ServerCapabilities Capabilities { get; set; }
    }
}
