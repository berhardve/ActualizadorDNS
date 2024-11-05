using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace IpUpdaterService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _ipFilePath;
        private readonly string _username; private readonly string _password;
        private readonly string _dnsName;
        private readonly List<string> _ipServices;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
             Environment.SetEnvironmentVariable("TF_CPP_MIN_LOG_LEVEL", "2"); // 0 = INFO, 1 = WARNING, 2 = ERROR, 3 = FATAL

            _logger = logger;
            _httpClient = new HttpClient();
            _ipFilePath = configuration["AppSettings:IpFilePath"] ?? "default_ultima_ip.txt";
            _username = configuration["AppSettings:LoginCredentials:Username"] ?? throw new ArgumentNullException("Username is required");
            _password = configuration["AppSettings:LoginCredentials:Password"] ?? throw new ArgumentNullException("Password is required");
            _dnsName = configuration["AppSettings:LoginCredentials:DnsName"] ?? throw new ArgumentNullException("Dns name is required");
            _ipServices = configuration.GetSection("IpServiceUrls").Get<List<string>>() ?? new List<string>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int retryCount = 0;
            TimeSpan delay = TimeSpan.FromMinutes(1);
            // Limpiar la consola al inicio de cada iteración
            Console.Clear();
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Iniciando proceso de actualización de IP en: {time}", DateTimeOffset.Now);
                try
                {
                    _logger.LogInformation("Obteniendo la IP pública...");
                    string? ipPublica = await ObtenerIpPublica();
                    if (string.IsNullOrEmpty(ipPublica))
                    {
                        _logger.LogWarning("No se pudo obtener la IP pública. Se omitirá esta ejecución.");
                        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                        continue;
                    }

                    _logger.LogInformation("IP pública obtenida: {ip}", ipPublica);

                    string? ipAnterior = LeerIpAnterior();
                    if (ipPublica == ipAnterior)
                    {
                        _logger.LogInformation("La IP no ha cambiado desde la última ejecución. Esperando para la próxima verificación...");
                        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                        continue;
                    }

                    _logger.LogInformation("La IP ha cambiado. Procediendo con la actualización...");

                    var options = new ChromeOptions();
                    options.AddArgument("--headless");
                    using IWebDriver driver = new ChromeDriver(options);
                    _logger.LogInformation("Navegando a la página de inicio de sesión...");

                    WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(60));
                    string urlLogin = "https://ngrcomputacion.cl:2222/evo/";
                    driver.Navigate().GoToUrl(urlLogin);

                    wait.Until(driver => ((IJavaScriptExecutor)driver).ExecuteScript("return document.readyState").Equals("complete"));
                    _logger.LogInformation("Documento cargado. Continuando...");

                    Thread.Sleep(5000);
                    _logger.LogInformation("Esperando campos de inicio de sesión...");
                    IWebElement usernameField = wait.Until(drv => drv.FindElement(By.XPath("//input[@name='username']")));
                    IWebElement passwordField = wait.Until(drv => drv.FindElement(By.XPath("//input[@name='password']")));
                    IWebElement loginButton = wait.Until(drv => drv.FindElement(By.ClassName("Button")));

                    _logger.LogInformation("Enviando credenciales...");
                    usernameField.SendKeys(_username);
                    passwordField.SendKeys(_password);
                    loginButton.Click();

                    Thread.Sleep(1000);

                    _logger.LogInformation("Navegando a la página de DNS...");
                    string urlDns = "https://ngrcomputacion.cl:2222/evo/user/dns";
                    driver.Navigate().GoToUrl(urlDns);
                    wait.Until(driver => ((IJavaScriptExecutor)driver).ExecuteScript("return document.readyState").Equals("complete"));
                    _logger.LogInformation("Documento de DNS cargado. Continuando...");

                    _logger.LogInformation("Esperando que la tabla de DNS se cargue...");
                    IWebElement tabla = wait.Until(drv => drv.FindElement(By.CssSelector("table")));
                    _logger.LogInformation("Tabla de DNS cargada. Procesando...");
                    Thread.Sleep(5000);

                    var rows = tabla.FindElements(By.CssSelector("tr"));
                    if (rows.Count == 0)
                    {
                        _logger.LogWarning("No se encontraron filas en la tabla de DNS.");
                        return;
                    }

                    _logger.LogInformation("Buscando la fila correspondiente...");
                    foreach (var row in rows)
                    {
                        var cells = row.FindElements(By.CssSelector("td"));
                        if (cells.Count > 1 && cells[1].Text == _dnsName)
                        {
                            _logger.LogInformation("Fila encontrada, haciendo clic en el botón de edición...");
                            IWebElement editarButton = cells.Last().FindElement(By.CssSelector("button"));
                            editarButton.Click();
                            break;
                        }
                    }

                    _logger.LogInformation("Esperando campo de texto para la IP...");
                    IWebElement valorInput = wait.Until(drv => drv.FindElement(By.CssSelector("input[type='text'][rows='3']")));
                    valorInput.Clear();
                    Thread.Sleep(7000);
                    valorInput.SendKeys(ipPublica);

                    _logger.LogInformation("Guardando cambios...");
                    IWebElement guardarButton = wait.Until(drv => drv.FindElement(By.XPath("//button[contains(., 'Guardar')]")));
                    guardarButton.Click();

                    _logger.LogInformation("Proceso de actualización completado con éxito en: {time}", DateTimeOffset.Now);

                    GuardarIp(ipPublica);

                    retryCount = 0;
                    delay = TimeSpan.FromMinutes(1);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Ocurrió un error: {error} - StackTrace: {stackTrace}", ex.Message, ex.StackTrace);
                    retryCount++;

                    if (retryCount >= 3)
                    {
                        delay = TimeSpan.FromMinutes(5); 

                    }
                }
                finally
                {
                    _logger.LogInformation("Se reintentará en {delay} minutos", delay.TotalMinutes);
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }

        private async Task<string?> ObtenerIpPublica()
        {
            foreach (var service in _ipServices)
            {
                try
                {
                    _logger.LogInformation("Consultando IP pública desde: {service}", service);
                    string ip = await _httpClient.GetStringAsync(service);
                    return ip.Trim();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Fallo al obtener IP desde {service}: {error}", service, ex.Message);
                }
            }

            _logger.LogError("No se pudo obtener la IP pública de los servicios disponibles.");
            return null;
        }

        private string? LeerIpAnterior()
        {
            try
            {
                if (File.Exists(_ipFilePath))
                {
                    return File.ReadAllText(_ipFilePath).Trim();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error al leer la IP anterior: {error}", ex.Message);
            }
            return null;
        }

        private void GuardarIp(string ip)
        {
            try
            {
                File.WriteAllText(_ipFilePath, ip);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error al guardar la IP actual: {error}", ex.Message);
            }
        }
    }
}
