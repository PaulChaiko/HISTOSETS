using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HystOSets
{
    /// <summary>
    /// Логика взаимодействия для INFO2.xaml
    /// </summary>
    public partial class INFO2 : Window
    {
        public INFO2(string Name, string info )
        {
            InitializeComponent();

            this.Title = Name;
            Page.Text = info;
        }
    }
}
