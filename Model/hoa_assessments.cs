using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class hoa_assessments
    {
        public string? id { get; set; }
        public int OwnerID { get; set; }        // Id: String(OwnerID + FY)
        public string? Parcel_ID { get; set; }   // Partition key
        public int FY { get; set; }             // Unique key:  /OwnerID,/Parcel_ID,/FY
        public string? DuesAmt { get; set; }
        public string? DateDue { get; set; }
        public bool DuesDue { get; set; }
        public int Paid { get; set; }
        public int NonCollectible { get; set; }
        public string? DatePaid { get; set; }
        public string? PaymentMethod { get; set; }
        public int Lien { get; set; }
        public string? LienRefNo { get; set; }
        public DateTime DateFiled { get; set; }
        //public string DateFiled { get; set; }
        public string? Disposition { get; set; }
        public decimal FilingFee { get; set; }
        public decimal ReleaseFee { get; set; }
        public DateTime DateReleased { get; set; }
        //public string DateReleased { get; set; }
        public DateTime LienDatePaid { get; set; }
        //public string LienDatePaid { get; set; }
        public decimal AmountPaid { get; set; }
        public int StopInterestCalc { get; set; }
        public decimal FilingFeeInterest { get; set; }
        public decimal AssessmentInterest { get; set; }
        public int InterestNotPaid { get; set; }
        public decimal BankFee { get; set; }
        public string? LienComment { get; set; }
        public string? Comments { get; set; }
        public string? LastChangedBy { get; set; }
        public DateTime LastChangedTs { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }

    }
}
