using System;
using System.Collections.Generic;
using System.Text;

namespace NoteinDesktopViewer.Models
{
    public class NoteFolder
    {
        public string ParentId { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
