
namespace grhaWebFunctions.Model
{
    public class Trustee
    {
        public required string id { get; set; }
        public int TrusteeId { get; set; }
        public string? Name { get; set; }
        public string? Position { get; set; }
        public string? PhoneNumber { get; set; }
        public string? Description { get; set; }
        public string? ImageUrl { get; set; }
        public string? WebsiteMessage { get; set; }
    }
}
