using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace Norristown.Server.Protocol
{
    [DataContract]
    public class InitializeParams
    {
        [DataMember(Name = "processId")]
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Int32? ProcessId { get; set; }
    }
}
