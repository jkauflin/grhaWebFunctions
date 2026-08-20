using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class HoaProperty
    {
        public string? parcelId { get; set; }
        public int lotNo { get; set; }
        public int subDivParcel { get; set; }
        public string? parcelLocation { get; set; }
	    public string? ownerName { get; set; }
	    public string? ownerPhone { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
