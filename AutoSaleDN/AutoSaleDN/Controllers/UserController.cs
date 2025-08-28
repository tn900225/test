﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AutoSaleDN.Models;
using BCrypt.Net;
using static AutoSaleDN.DTO.Auth;
using MimeKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Authorization;
using System.Globalization;

[Route("api/[controller]")]
[ApiController]
public class UserController : ControllerBase
{
    private readonly AutoSaleDbContext _context;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;

    public UserController(AutoSaleDbContext context, IConfiguration config, IMemoryCache cache)
    {
        _context = context;
        _config = config;
        _cache = cache;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto model)
    {
        if (await _context.Users.AnyAsync(x => x.Name == model.Name || x.Email == model.Email))
            return BadRequest("Username or email already exists.");

        var user = new User
        {
            Name = model.Name,
            Email = model.Email,
            FullName = model.FullName,
            Mobile = model.Mobile,
            Role = model.Role,
            Password = BCrypt.Net.BCrypt.HashPassword(model.Password),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Province = model.Province,
            Status = true
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return Ok(new { message = "Registration successful" });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto model)
    {
        var user = await _context.Users.SingleOrDefaultAsync(x => x.Email == model.Email);
        if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.Password))
        {
            return Unauthorized("Invalid email or password.");
        }
        if (!user.Status)
        {
            return Unauthorized("Your account has been deactivated by the administrator.");
        }

        var token = GenerateJwtToken(user);
        return Ok(new
        {
            token,
            role = user.Role
        });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> GetCurrentUser()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var user = await _context.Users
            .Where(u => u.UserId == Int32.Parse(userId))
            .Select(u => new UserDto
            {
                UserId = u.UserId,
                Name = u.Name,
                Email = u.Email,
                FullName = u.FullName,
                Mobile = u.Mobile,
                Province = u.Province,
                Role = u.Role
            })
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound();
        }

        return Ok(user);
    }

    [HttpPost("forgotpassword")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto model)
    {
        var user = await _context.Users.SingleOrDefaultAsync(x => x.Email == model.Email);
        if (user == null)
            return NotFound("Email not found.");

        var otp = new Random().Next(100_000, 999_999).ToString();

        _cache.Set(
            $"reset_otp_{user.Email}",
            otp,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            }
        );

        await SendEmailAsync(
            user.Email,
            "Your Password Reset Code",
            $"Your password reset code is: {otp}. It expires in 10 minutes."
        );

        return Ok(new { message = "Reset code sent to your email." });
    }

    [HttpPost("verify-reset-otp")]
    public IActionResult VerifyResetOtp([FromBody] VerifyOtpDto model)
    {
        if (!_cache.TryGetValue($"reset_otp_{model.Email}", out string cachedOtp) || cachedOtp != model.Otp)
        {
            return BadRequest("Invalid or expired code.");
        }
        return Ok(new { message = "OTP valid, you can reset password now." });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto model)
    {
        if (!_cache.TryGetValue($"reset_otp_{model.Email}", out string cachedOtp) || cachedOtp != model.Otp)
        {
            return BadRequest("Invalid or expired code.");
        }

        var user = await _context.Users.SingleOrDefaultAsync(x => x.Email == model.Email);
        if (user == null)
            return NotFound("Email not found.");

        user.Password = BCrypt.Net.BCrypt.HashPassword(model.NewPassword);
        await _context.SaveChangesAsync();

        _cache.Remove($"reset_otp_{model.Email}");

        return Ok(new { message = "Password reset successful." });
    }

    [HttpGet("cars")]
    public async Task<IActionResult> GetCars(
        [FromQuery] string? keyword = null,
        [FromQuery] string? paymentType = null,
        [FromQuery] decimal? priceFrom = null,
        [FromQuery] decimal? priceTo = null,
        [FromQuery] bool? vatDeduction = null,
        [FromQuery] bool? discountedCars = null,
        [FromQuery] bool? premiumPartners = null,
        [FromQuery] int? registrationFrom = null,
        [FromQuery] int? registrationTo = null,
        [FromQuery] int? mileageFrom = null,
        [FromQuery] int? mileageTo = null,
        [FromQuery] string? transmission = null,
        [FromQuery] string? fuelType = null,
        [FromQuery] string? powerUnit = null,
        [FromQuery] double? powerFrom = null,
        [FromQuery] double? powerTo = null,
        [FromQuery] string? vehicleType = null,
        [FromQuery] bool? driveType4x4 = null,
        [FromQuery] string? color = null,
        [FromQuery] List<string>? features = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 5 
    )
    {
        var query = _context.CarListings
            .Include(c => c.Model)
                .ThenInclude(m => m.CarManufacturer)
            .Include(c => c.Specifications)
            .Include(c => c.CarImages)
            .Include(c => c.CarListingFeatures)
                .ThenInclude(clf => clf.Feature)
            .Include(c => c.CarServiceHistories)
            .Include(c => c.CarPricingDetails)
            .Include(c => c.StoreListings)
                .ThenInclude(sl => sl.CarSales)
                    .ThenInclude(cs => cs.SaleStatus)
            .Include(c => c.Reviews)
                .ThenInclude(r => r.User)
            .Include(c => c.StoreListings)
                .ThenInclude(cs => cs.StoreLocation)
            .AsQueryable();

        // Apply Keyword Filter
        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(c =>
                c.Model.Name.Contains(keyword) ||
                c.Model.CarManufacturer.Name.Contains(keyword) ||
                c.Description.Contains(keyword) ||
                c.Year.ToString().Contains(keyword)
            );
        }
        if (priceFrom.HasValue)
        {
            query = query.Where(c => c.Price >= priceFrom.Value);
        }
        if (priceTo.HasValue)
        {
            query = query.Where(c => c.Price <= priceTo.Value);
        }
        if (vatDeduction.HasValue && vatDeduction.Value)
        {
            query = query.Where(c => c.CarPricingDetails.Any());
        }
        if (discountedCars.HasValue && discountedCars.Value)
        {
        }
        if (premiumPartners.HasValue && premiumPartners.Value)
        {
        }
        if (registrationFrom.HasValue)
        {
            query = query.Where(c => c.Year >= registrationFrom.Value);
        }
        if (registrationTo.HasValue)
        {
            query = query.Where(c => c.Year <= registrationTo.Value);
        }
        if (mileageFrom.HasValue)
        {
            query = query.Where(c => c.Mileage >= mileageFrom.Value);
        }
        if (mileageTo.HasValue)
        {
            query = query.Where(c => c.Mileage <= mileageTo.Value);
        }
        if (!string.IsNullOrEmpty(transmission))
        {
            query = query.Where(c => c.Specifications.Any(s => s.Transmission == transmission));
        }
        if (!string.IsNullOrEmpty(fuelType))
        {
            query = query.Where(c => c.Specifications.Any(s => s.FuelType == fuelType));
        }
        if (!string.IsNullOrEmpty(vehicleType))
        {
            query = query.Where(c => c.Specifications.Any(s => s.CarType == vehicleType));
        }
        if (driveType4x4.HasValue && driveType4x4.Value)
        {
            query = query.Where(c => c.CarListingFeatures.Any(clf => clf.Feature.Name == "4x4"));
        }
        if (!string.IsNullOrEmpty(color))
        {
            query = query.Where(c => c.Specifications.Any(s => s.ExteriorColor != null && s.InteriorColor != null));
        }
        if (features != null && features.Any())
        {
            foreach (var featureName in features)
            {
                query = query.Where(c => c.CarListingFeatures.Any(clf => clf.Feature.Name == featureName));
            }
        }

        query = query.Where(c =>

        !c.StoreListings.Any() ||
        c.StoreListings
            .SelectMany(sl => sl.CarSales)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault() == null ||
        c.StoreListings
            .SelectMany(sl => sl.CarSales)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault()
            .SaleStatus.StatusName != "Payment Complete"
            );
        query = query.OrderByDescending(c => c.DatePosted);

        var totalResults = await query.CountAsync();

        var carsToSkip = (page - 1) * perPage;
        query = query.Skip(carsToSkip).Take(perPage);


        var cars = await query
            .Select(c => new
            {
                c.ListingId,
                c.ModelId,
                c.UserId,
                c.Year,
                c.Mileage,
                c.Price,
                c.Condition,
                c.DatePosted,
                c.Description,
                Model = new
                {
                    c.Model.ModelId,
                    c.Model.Name,
                    Manufacturer = new
                    {
                        c.Model.CarManufacturer.ManufacturerId,
                        c.Model.CarManufacturer.Name
                    }
                },
                Specifications = c.Specifications != null ? c.Specifications.Select(s => new
                {
                    s.SpecificationId,
                    s.Engine,
                    s.Transmission,
                    s.FuelType,
                    s.SeatingCapacity,
                    s.CarType
                }).ToList() : null,
                Images = c.CarImages != null ? c.CarImages.Select(i => new
                {
                    i.ImageId,
                    i.Url,
                    i.Filename
                }) : null,
                Features = c.CarListingFeatures != null ? c.CarListingFeatures.Select(f => new
                {
                    f.Feature.FeatureId,
                    f.Feature.Name
                }) : null,
                ServiceHistory = c.CarServiceHistories != null ? c.CarServiceHistories.Select(sh => new
                {
                    sh.RecentServicing,
                    sh.NoAccidentHistory,
                    sh.Modifications
                }) : null,
                Pricing = c.CarPricingDetails != null ? c.CarPricingDetails.Select(shh => new
                {
                    shh.TaxRate,
                    shh.RegistrationFee
                }).ToList() : null,
                SalesHistory = c.CarSales != null ? c.CarSales.Select(s => new
                {
                    s.SaleId,
                    s.FinalPrice,
                    s.SaleDate,
                    s.SaleStatus.StatusName
                }) : null,
                Reviews = c.Reviews != null ? c.Reviews.Select(r => new
                {
                    r.ReviewId,
                    r.UserId,
                    r.Rating,
                    r.User.FullName,
                    r.CreatedAt
                }) : null,
                Showrooms = c.StoreListings != null ? c.StoreListings.Select(cs => new
                {
                    cs.StoreLocation.StoreLocationId,
                    cs.StoreLocation.Name,
                    cs.StoreLocation.Address,
                }) : null,
                currentSaleStatus = c.StoreListings
                .SelectMany(sl => sl.CarSales)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => s.SaleStatus.StatusName)
                .FirstOrDefault() ?? "Available"
            })
            .ToListAsync();
        var totalPages = (int)Math.Ceiling((double)totalResults / perPage);
        if (totalPages == 0 && totalResults > 0) totalPages = 1;

        return Ok(new
        {
            cars = cars,
            totalResults = totalResults,
            totalPages = totalPages
        });
    }

    [HttpGet("cars/{id}")]
    public async Task<IActionResult> GetCarDetail(int id)
    {
        var car = await _context.CarListings
            .Include(c => c.Model)
                .ThenInclude(m => m.CarManufacturer)
            .Include(c => c.Specifications)
            .Include(c => c.CarImages)
            .Include(c => c.CarVideos)
            .Include(c => c.CarListingFeatures)
                .ThenInclude(clf => clf.Feature)
            .Include(c => c.CarServiceHistories)
            .Include(c => c.CarPricingDetails)
            .Include(c => c.StoreListings)
                .ThenInclude(sl => sl.CarSales)
                    .ThenInclude(cs => cs.SaleStatus)
            .Include(c => c.StoreListings)
                .ThenInclude(sl => sl.CarSales)
                    .ThenInclude(cs => cs.DepositPayment)
            .Include(c => c.StoreListings)
                .ThenInclude(sl => sl.CarSales)
                    .ThenInclude(cs => cs.FullPayment)
            .Include(c => c.Reviews)
                .ThenInclude(r => r.User)
            .Include(c => c.StoreListings)
                .ThenInclude(cs => cs.StoreLocation)
            .FirstOrDefaultAsync(c => c.ListingId == id);

        if (car == null)
            return NotFound(new { message = "Car not found." });

        var allCarSalesForThisCar = car.StoreListings?
            .SelectMany(sl => sl.CarSales ?? new List<CarSale>())
            .ToList();

        var latestRelevantSale = allCarSalesForThisCar?
        .OrderByDescending(s => s.CreatedAt)
        .FirstOrDefault(s =>
            s.SaleStatus?.StatusName == "Deposit Paid" ||
            s.SaleStatus?.StatusName == "Payment Complete" ||
            s.SaleStatus?.StatusName == "Pending Deposit" ||
            s.SaleStatus?.StatusName == "Pending Full Payment"
        );

        string saleStatusDisplay = "Available";
        string paymentStatusDisplay = null;

        if (latestRelevantSale != null)
        {
            if (latestRelevantSale.SaleStatus?.StatusName == "Payment Complete")
            {
                saleStatusDisplay = "Sold";
                paymentStatusDisplay = "Full Payment Made";
            }
            else if (latestRelevantSale.SaleStatus?.StatusName == "Deposit Paid")
            {
                saleStatusDisplay = "On Hold";
                paymentStatusDisplay = "Deposit Made";
            }
            else if (latestRelevantSale.SaleStatus?.StatusName == "Pending Deposit")
            {
                saleStatusDisplay = "Pending Deposit";
                paymentStatusDisplay = "Pending Deposit Payment";
            }
            else if (latestRelevantSale.SaleStatus?.StatusName == "Pending Full Payment")
            {
                saleStatusDisplay = "Pending Full Payment";
                paymentStatusDisplay = "Pending Full Payment";
            }
        }


        var carDetail = new
        {
            car.ListingId,
            car.ModelId,
            car.UserId,
            car.Year,
            car.Mileage,
            car.Price,
            car.Condition,
            car.DatePosted,
            car.Description,
            Model = new
            {
                car.Model.ModelId,
                car.Model.Name,
                Manufacturer = new
                {
                    car.Model.CarManufacturer.ManufacturerId,
                    car.Model.CarManufacturer.Name
                }
            },
            Specification = car.Specifications != null ? car.Specifications.Select(s => new
            {
                s.SpecificationId,
                s.Engine,
                s.Transmission,
                s.FuelType,
                s.SeatingCapacity,
                s.CarType
            }).ToList() : null,
            Images = car.CarImages != null ? car.CarImages.Select(i => new
            {
                i.ImageId,
                i.Url,
                i.Filename
            }) : null,
            CarVideo = car.CarVideos != null ? car.CarVideos.Select(i => new
            {
                i.VideoId,
                i.Url,
                i.ListingId
            }) : null,
            Features = car.CarListingFeatures != null ? car.CarListingFeatures.Select(f => new
            {
                f.Feature.FeatureId,
                f.Feature.Name
            }) : null,
            ServiceHistory = car.CarServiceHistories != null ? car.CarServiceHistories.Select(sh => new
            {
                sh.RecentServicing,
                sh.NoAccidentHistory,
                sh.Modifications
            }) : null,
            Pricing = car.CarPricingDetails != null ? car.CarPricingDetails.Select(shh => new
            {
                shh.TaxRate,
                shh.RegistrationFee
            }).ToList() : null,
            SalesHistory = car.CarSales != null ? car.CarSales.Select(s => new
            {
                s.SaleId,
                s.FinalPrice,
                s.SaleDate,
                s.SaleStatus.StatusName
            }) : null,
            Reviews = car.Reviews != null ? car.Reviews.Select(r => new
            {
                r.ReviewId,
                r.UserId,
                r.Rating,
                r.User.FullName,
                r.CreatedAt
            }) : null,
            Showrooms = car.StoreListings != null ? car.StoreListings.Select(cs => new
            {
                cs.StoreLocation.StoreLocationId,
                cs.StoreLocation.Name,
                cs.StoreLocation.Address,
            }) : null,
            CurrentSaleStatus = saleStatusDisplay,
            CurrentPaymentStatus = paymentStatusDisplay
        };

        return Ok(carDetail);
    }

    [HttpGet("cars/{id}/similar")]
    public async Task<IActionResult> GetSimilarCars(int id)
    {
        var car = await _context.CarListings
            .Include(c => c.Model)
                .ThenInclude(m => m.CarManufacturer)
            .FirstOrDefaultAsync(c => c.ListingId == id);

        if (car == null)
            return NotFound(new { message = "Car not found." });

        var similarCars = await _context.CarListings
            .Include(c => c.Model)
                .ThenInclude(m => m.CarManufacturer)
            .Include(c => c.Specifications)
            .Include(c => c.CarImages)
            .Include(c => c.CarListingFeatures)
                .ThenInclude(clf => clf.Feature)
            .Where(c => c.Model.ManufacturerId == car.Model.ManufacturerId && c.ListingId != id)
            .Take(3)
            .Select(c => new
            {
                c.ListingId,
                Name = $"{c.Model.CarManufacturer.Name} {c.Model.Name}",
                Image = c.CarImages != null ? c.CarImages.Select(i => i.Url).FirstOrDefault() : null,
                c.Price,
                Details = c.Specifications != null ? c.Specifications.Select(s => new
                {
                    s.Engine,
                    s.Transmission,
                    s.FuelType
                }).FirstOrDefault() : null,
                Tags = c.CarListingFeatures != null ? c.CarListingFeatures.Select(f => f.Feature.Name).Take(2).ToList() : new List<string>()
            })
            .ToListAsync();

        return Ok(similarCars);
    }


    private string GenerateJwtToken(User user)
    {
        var jwtSettings = _config.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Key"]));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        };

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(Convert.ToDouble(jwtSettings["ExpireMinutes"])),
            signingCredentials: creds
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task SendEmailAsync(string toEmail, string subject, string body)
    {
        var emailSettings = _config.GetSection("EmailSettings");
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(emailSettings["SenderName"], emailSettings["SenderEmail"]));
        email.To.Add(MailboxAddress.Parse(toEmail));
        email.Subject = subject;
        email.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(emailSettings["SmtpServer"], int.Parse(emailSettings["Port"]), false);
        await smtp.AuthenticateAsync(emailSettings["SenderEmail"], emailSettings["SenderPassword"]);
        await smtp.SendAsync(email);
        await smtp.DisconnectAsync(true);
    }
    [HttpGet("cars/years")]
    public async Task<IActionResult> GetDistinctRegistrationYears()
    {
        var years = await _context.CarListings
            .Select(c => c.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToListAsync();
        return Ok(years);
    }
    [HttpGet("cars/features")]
    public async Task<IActionResult> GetDistinctFeatures()
    {
        var features = await _context.CarFeatures
            .Select(f => f.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync();
        return Ok(features);
    }
    [HttpGet("cars/vehicle-types")]
    public async Task<IActionResult> GetDistinctVehicleTypes()
    {
        var vehicleTypes = await _context.CarListings
            .Where(c => c.Specifications.Any())
            .SelectMany(c => c.Specifications)
            .Select(s => s.CarType)
            .Where(type => type != null && type != "")
            .Distinct()
            .OrderBy(type => type)
            .ToListAsync();
        return Ok(vehicleTypes);
    }
    [HttpGet("cars/fuel-types")]
    public async Task<IActionResult> GetDistinctFuelTypes()
    {
        var fuelTypes = await _context.CarListings
            .Where(c => c.Specifications.Any())
            .SelectMany(c => c.Specifications)
            .Select(s => s.FuelType)
            .Where(type => type != null && type != "")
            .Distinct()
            .OrderBy(type => type)
            .ToListAsync();
        return Ok(fuelTypes);
    }

    [HttpGet("cars/mileage-ranges")]
    public async Task<IActionResult> GetMileageRanges()
    {
        var minMileage = await _context.CarListings
            .MinAsync(c => (int?)c.Mileage);
        var maxMileage = await _context.CarListings
            .MaxAsync(c => (int?)c.Mileage);

        if (!minMileage.HasValue || !maxMileage.HasValue)
        {
            return Ok(new List<object>()); 
        }

        var breakpoints = new List<int> { 10000, 50000, 100000, 150000 };
        breakpoints.Sort();

        var ranges = new List<object>();

        int currentMin = 0;

        if (minMileage.Value > 0)
        {
            ranges.Add(new { value = $"0-{minMileage.Value}", label = $"0 - {minMileage.Value:N0} km" });
            currentMin = minMileage.Value;
        }


        foreach (var bp in breakpoints)
        {
            if (currentMin < bp)
            {
                int rangeTo = Math.Min(bp, maxMileage.Value);
                if (currentMin < rangeTo)
                {
                    ranges.Add(new { value = $"{currentMin}-{rangeTo}", label = $"{currentMin:N0} - {rangeTo:N0} km" });
                }
            }
            currentMin = bp + 1;
        }

        if (maxMileage.Value >= currentMin)
        {
            ranges.Add(new { value = $"{currentMin}-max", label = $"Trên {currentMin:N0} km" });
        }

        if (ranges.Count == 0 && minMileage.HasValue && maxMileage.HasValue)
        {
            ranges.Add(new { value = $"{minMileage.Value}-{maxMileage.Value}", label = $"{minMileage.Value:N0} - {maxMileage.Value:N0} km" });
        }


        return Ok(ranges);
    }

    [HttpGet("cars/price-ranges")]
    public async Task<IActionResult> GetPriceRanges()
    {
        var minPrice = await _context.CarListings
            .MinAsync(c => (decimal?)c.Price); 
        var maxPrice = await _context.CarListings
            .MaxAsync(c => (decimal?)c.Price); 

        if (!minPrice.HasValue || !maxPrice.HasValue)
        {
            return Ok(new List<object>());
        }

        var ranges = new List<object>();
        decimal currentMin = minPrice.Value;

        decimal[] potentialBreakpoints = new decimal[]
        {
        0m,             
        50_000_000m,    
        100_000_000m,   
        200_000_000m,   
        300_000_000m,   
        500_000_000m,   
        700_000_000m,   
        1_000_000_000m, 
        1_500_000_000m, 
        2_000_000_000m, 
        3_000_000_000m, 
        5_000_000_000m, 
                        
        };

        var relevantBreakpoints = potentialBreakpoints
            .Where(bp => bp >= 0m && bp >= currentMin && bp <= maxPrice.Value) 
            .OrderBy(bp => bp)
            .ToList();

        if (currentMin > 0m && (relevantBreakpoints.Count == 0 || relevantBreakpoints[0] > currentMin))
        {
            decimal firstRangeTo = relevantBreakpoints.Any() ? Math.Min(relevantBreakpoints[0], maxPrice.Value) : maxPrice.Value;
            if (0m < firstRangeTo)
            {
                ranges.Add(new { value = $"0-{firstRangeTo}", label = $"0 - {FormatCurrency(firstRangeTo)} VND" });
                currentMin = firstRangeTo + 1m; 
            }
        }
        else if (currentMin == 0m && relevantBreakpoints.Any())
        {

            currentMin = 0m;
        }

        foreach (var bp in relevantBreakpoints)
        {
            if (currentMin < bp) 
            {
                ranges.Add(new { value = $"{currentMin}-{bp}", label = $"{FormatCurrency(currentMin)} - {FormatCurrency(bp)} VND" });
                currentMin = bp + 1m; 
            }
        }

        if (maxPrice.Value >= currentMin)
        {
            ranges.Add(new { value = $"{currentMin}-max", label = $"Above {FormatCurrency(currentMin)} VND" });
        }

        if (ranges.Count == 0 && minPrice.HasValue && maxPrice.HasValue)
        {
            ranges.Add(new { value = $"{minPrice.Value}-{maxPrice.Value}", label = $"{FormatCurrency(minPrice.Value)} - {FormatCurrency(maxPrice.Value)} VND" });
        }

        return Ok(ranges);
    }

    private string FormatCurrency(decimal amount)
    {
        if (amount >= 1_000_000_000m)
        {
            decimal billion = amount / 1_000_000_000m;
            return $"{billion:0.#} billion";
        }
        else if (amount >= 1_000_000m)
        {
            decimal million = amount / 1_000_000m;
            return $"{million:0.#} million";
        }
        else if (amount >= 1_000m)
        {
            decimal thousand = amount / 1_000m;
            return $"{thousand:0.#} thousand";
        }
     
        return amount.ToString("N0", CultureInfo.InvariantCulture);
    }

}

public class TestDriveRequestDto
{
    public int ShowroomId { get; set; }
    public string Name { get; set; }
    public string Phone { get; set; }
    public string Email { get; set; }
    public DateTime PreferredDate { get; set; }
}

public class TestDrive
{
    public int TestDriveId { get; set; }
    public int CarListingId { get; set; }
    public int ShowroomId { get; set; }
    public string Name { get; set; }
    public string Phone { get; set; }
    public string Email { get; set; }
    public DateTime PreferredDate { get; set; }
    public DateTime CreatedAt { get; set; }
}