using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class hoa_properties2
    {
        public string? id { get; set; }
        public string? Parcel_ID { get; set; }       // Id and Partition key
        public int LotNo { get; set; }
        public int SubDivParcel { get; set; }
        public string? Parcel_Location { get; set; }
        public int Property_Street_No { get; set; }
        public string? Property_Street_Name { get; set; }
        public string? Property_City { get; set; }
        public string? Property_State { get; set; }
        public string? Property_Zip { get; set; }
        public int Member { get; set; }
        public int Vacant { get; set; }
        public int Rental { get; set; }
        public int Managed { get; set; }
        public int Foreclosure { get; set; }
        public int Bankruptcy { get; set; }
        public int Liens_2B_Released { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
