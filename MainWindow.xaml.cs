using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml;


namespace HystOSets
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window

    {
        string ATLAS = "ATLAS/ATLAS.xml";
        List<string> LSpecimens = new List<string>();
        List<string> LElements = new List<string>();
        List<string> LPoints = new List<string>();
        XmlDocument xDoc = new XmlDocument();

        List<Polygon> LPolygons = new List<Polygon>();

        string CurrentSpecimen;
        String CurrentElement;

        string logFilePath = "ATLAS/L.txt";


        public MainWindow()
        {
            InitializeComponent();



            xDoc.Load(ATLAS);
            XmlElement xRoot = xDoc.DocumentElement;
            foreach (XmlElement node in xRoot) LSpecimens.Add(node.Attributes.GetNamedItem("NAME").Value.ToString());
            foreach (string S in LSpecimens) ListOfSpecimens.Items.Add(S);




        }

        private void ListBoxItem_DoubleClick(object sender, MouseButtonEventArgs e)
        {

            LElements.Clear();
            CurrentElement = null;
            AboutS.Text = null;
            AboutE.Text = null;
            var listBox = (ListBox)sender;
            if (listBox.SelectedItem != null)
            {

                CurrentSpecimen = listBox.SelectedItem.ToString();
                LPoints.Clear();

                foreach (Polygon P in LPolygons)
                {
                    Desk.Children.Remove(P);
                }
                LPolygons.Clear();



                listBox = (ListBox)sender;
                var selectedItem = listBox.SelectedItem;


                if (selectedItem != null)
                {
                    string SP = selectedItem.ToString();
                    CurrentSpecimen = SP;

                    string Img = "";


                    xDoc.Load(ATLAS);
                    XmlElement xRoot = xDoc.DocumentElement;
                    foreach (XmlElement node in xRoot)
                    {
                        if (node.Attributes.GetNamedItem("NAME").Value.ToString() == SP)
                        {
                            AboutS.Text = node.Attributes.GetNamedItem("INFO1").Value.ToString();
                            Img = node.Attributes.GetNamedItem("IMAGE").Value.ToString();

                            foreach (XmlElement child in node) LElements.Add(child.Attributes.GetNamedItem("NAME").Value.ToString());
                        }

                    }

                    var U = new Uri($"pack://application:,,,/SPECIMENS/{Img}");
                    var bitmap = new BitmapImage(U);
                    Specimen.Source = bitmap;

                    ListOfElements.Items.Clear();

                    foreach (string S in LElements)
                    {
                        ListOfElements.Items.Add(S);
                    }

                    LPoints.Clear();

                    foreach (Polygon P in LPolygons)
                    {
                        Desk.Children.Remove(P);
                    }
                    LPolygons.Clear();


                }
            }
        }

        private void ListBoxItem_DoubleClick2(object sender, MouseButtonEventArgs e)
        {

            LPoints.Clear();

            foreach (Polygon P in LPolygons)
            {
                Desk.Children.Remove(P);
            }
            LPolygons.Clear();

            var listBox = (ListBox)sender;
            var selectedItem = listBox.SelectedItem;


            if (selectedItem != null)
            {
                CurrentElement = selectedItem.ToString();
                xDoc.Load(ATLAS);
                XmlElement xRoot = xDoc.DocumentElement;
                foreach (XmlElement node in xRoot)
                {
                    if (node.Attributes.GetNamedItem("NAME").Value.ToString() == CurrentSpecimen)
                        foreach (XmlElement child in node)
                            if (child.Attributes.GetNamedItem("NAME").Value.ToString() == CurrentElement)

                            {
                                AboutE.Text = child.Attributes.GetNamedItem("INFO1").Value.ToString();
                                foreach (XmlElement child2 in child) LPoints.Add(child2.Attributes.GetNamedItem("POINTS").Value.ToString());
                            }
                }


                foreach (string P in LPoints)
                {
                    string[] Points;
                    string[] Points2;
                    List<string> Points3 = new List<string>();
                    List<double> Points4 = new List<double>();
                    Points = P.Split(" ");

                    foreach (string p in Points)
                    {
                        Points2 = p.Split(",");

                        foreach (string s in Points2) Points3.Add(s);

                    }


                    foreach (string s in Points3)
                    {
                        double N = Convert.ToDouble(s);
                        Points4.Add(N);

                    }

                    int jj = Points4.Count();

                    Polygon NewP = new Polygon();
                    PointCollection points = new PointCollection();


                    for (int j = 0; j < jj; j += 2)
                    {
                        points.Add(new Point(Points4[j], Points4[j + 1]));
                    }

                    NewP.Points = points;
                    NewP.Fill = Brushes.Aqua;
                    NewP.Opacity = 0.5;

                    LPolygons.Add(NewP);


                }

                foreach (Polygon P in LPolygons)
                {
                    Grid.SetZIndex(P, 10);
                    Desk.Children.Add(P);

                }

            }

        }

        private void Image_MouseDown(object sender, MouseButtonEventArgs e)
        {
            //Point clickPosition = e.GetPosition(Specimen);
            //string coordinates = $"{clickPosition.X:F0},{clickPosition.Y:F0}";      
            //    File.AppendAllText(logFilePath, coordinates+ " ");

        }

        private void SInfo_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentSpecimen != null)
            {
                string info = "";

                xDoc.Load(ATLAS);
                XmlElement xRoot = xDoc.DocumentElement;
                foreach (XmlElement node in xRoot) if (node.Attributes.GetNamedItem("NAME").Value.ToString() == CurrentSpecimen) info = node.Attributes.GetNamedItem("INFO2").Value.ToString();


                var _info2 = new INFO2(CurrentSpecimen, info);
                _info2.Show();
            }

        }

        private void EInfo_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentElement != null)
            {
                string info = "";

                xDoc.Load(ATLAS);
                XmlElement xRoot = xDoc.DocumentElement;
                foreach (XmlElement node in xRoot)
                {
                    if (node.Attributes.GetNamedItem("NAME").Value.ToString() == CurrentSpecimen)
                    {
                        foreach (XmlNode childnode in node) if (childnode.Attributes.GetNamedItem("NAME").Value.ToString() == CurrentElement) info = childnode.Attributes.GetNamedItem("INFO2").Value.ToString();

                    }
                }

                var _info2 = new INFO2(CurrentElement, info);
                _info2.Show();

            }

        }
    }
}