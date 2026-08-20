using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;


namespace grhaWebFunctions.Model
{
    public class hoa_communications
    {
        public string? id { get; set; }
        public string? Parcel_ID { get; set; }       // Partition key
        public int CommID { get; set; }             // Id
        public DateTime CreateTs { get; set; }
        public int OwnerID { get; set; }
        public string? CommType { get; set; }
        public string? CommDesc { get; set; }
        public string? Mailing_Name { get; set; }
        public byte Email { get; set; }
        public string? EmailAddr { get; set; }
        public string? SentStatus { get; set; }
        public string? LastChangedBy { get; set; }
        public DateTime LastChangedTs { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }

    }
}
