using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class DuesEmailEvent
    {
        public string? id { get; set; }   
        public string? parcelId { get; set; }   
        public string? emailAddr { get; set; }   
        public string? hoaName { get; set; }   
        public string? hoaNameShort { get; set; }   
        public string? hoaAddress1 { get; set; }   
        public string? hoaAddress2 { get; set; }   
        public string? helpNotes { get; set; }
        public string? duesUrl { get; set; }
        public string? mailType { get; set; }
        public string? mailSubject { get; set; }
        public string? htmlMessage { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

}
