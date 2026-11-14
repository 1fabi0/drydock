using System.Text.Json.Serialization;
using k8s;
using k8s.Models;

namespace DryDock.Util.Security
{

    [KubernetesEntity(Group = "cert-manager.io", Kind = "CertificateRequest", ApiVersion = "v1", PluralName = "certificaterequests")]
    public class V1CertificateRequest : IKubernetesObject<V1ObjectMeta>
    {
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = "cert-manager.io/v1";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "CertificateRequest";

        [JsonPropertyName("metadata")]
        public V1ObjectMeta Metadata { get; set; } = new();

        [JsonPropertyName("spec")]
        public CertificateRequestSpec Spec { get; set; } = new();

        [JsonPropertyName("status")]
        public CertificateRequestStatus? Status { get; set; }
    }

    public class CertificateRequestSpec
    {
        [JsonPropertyName("request")]
        public string Request { get; set; } = string.Empty; // Base64 PEM encoded CSR

        [JsonPropertyName("duration")]
        public string? Duration { get; set; } = "90d";

        [JsonPropertyName("isCA")]
        public bool IsCA { get; set; } = false;

        [JsonPropertyName("usages")]
        public List<string> Usages { get; set; } = new() { "server auth", "client auth" };

        [JsonPropertyName("issuerRef")]
        public ObjectReference IssuerRef { get; set; } = new();
    }

    public class ObjectReference
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "drydock-ca-issuer";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "Issuer"; // oder "Issuer"

        [JsonPropertyName("group")]
        public string Group { get; set; } = "cert-manager.io";
    }

    public class CertificateRequestStatus
    {
        [JsonPropertyName("certificate")]
        public string? Certificate { get; set; } // Base64 PEM

        [JsonPropertyName("ca")]
        public string? CA { get; set; } // Base64 PEM

        [JsonPropertyName("conditions")]
        public List<CertificateRequestCondition>? Conditions { get; set; }
    }

    public class CertificateRequestCondition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty; // "Ready", "Denied", "InvalidRequest"

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty; // "True", "False", "Unknown"

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
