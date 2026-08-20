using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class HoaProperty2
    {
        public string? parcelId { get; set; }
        public int lotNo { get; set; }
        public int subDivParcel { get; set; }
        public string? parcelLocation { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
