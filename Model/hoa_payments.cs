using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class hoa_payments
    {
        public string? id { get; set; }
        public string? Parcel_ID { get; set; }           // Partition key
        public int OwnerID { get; set; }
        public int FY { get; set; }
        public string? txn_id { get; set; }
        public string? payment_date { get; set; }
        public string? payer_email { get; set; }
        public decimal payment_amt { get; set; }
        public decimal payment_fee { get; set; }
        public DateTime LastChangedTs { get; set; }
        public string? paidEmailSent { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
