using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class hoa_owners
    {
        public string? id { get; set; }
        public int OwnerID { get; set; }            // Id
        public string? Parcel_ID { get; set; }       // Partition key
        public int CurrentOwner { get; set; }
        public string? Owner_Name1 { get; set; }
        public string? Owner_Name2 { get; set; }
        public string? DatePurchased { get; set; }
        public string? Mailing_Name { get; set; }
        public int AlternateMailing { get; set; }
        public string? Alt_Address_Line1 { get; set; }
        public string? Alt_Address_Line2 { get; set; }
        public string? Alt_City { get; set; }
        public string? Alt_State { get; set; }
        public string? Alt_Zip { get; set; }
        public string? Owner_Phone { get; set; }
        public string? EmailAddr { get; set; }
        public string? EmailAddr2 { get; set; }
        public string? Comments { get; set; }
        public string? EntryTimestamp { get; set; }
        public string? UpdateTimestamp { get; set; }
        public string? LastChangedBy { get; set; }
        public DateTime LastChangedTs { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }

    }
}
