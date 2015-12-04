﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Controls;


namespace com.vtcsecure.ace.windows.CustomControls.UnifiedSettings
{
    public delegate void UnifiedSettings_ContentChanging(UnifiedSettingsContentType contentType);


    public class BaseUnifiedSettingsPanel : UserControl
    {
        public string Title { get; set; }
        // Call When Panel Content needs to change
        public event UnifiedSettings_ContentChanging ContentChanging;

        // Invoke the Content Changed event
        public virtual void OnContentChanging(UnifiedSettingsContentType contentType)
        {
            if (ContentChanging != null)
            {
                ContentChanging(contentType);
            }
        }

        public virtual void SaveData()
        {

        }

    }
}
