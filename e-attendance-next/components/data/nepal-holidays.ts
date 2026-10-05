import { bsToAd, toAdString } from "@/components/date/bs-converter";

export type OfficialNepalHoliday = {
  name: string;
  bsYear: number;
  bsMonth: number;
  bsDay: number;
  description: string;
  isRecurring?: boolean;
};

/**
 * Official Gazette Public Holidays for Nepal (Government of Nepal / MoHA)
 * Covers years 2081 BS (2024/25), 2082 BS (2025/26), and 2083 BS (2026/27).
 */
export const NEPAL_PUBLIC_HOLIDAYS_CATALOG: OfficialNepalHoliday[] = [
  // ── 2081 BS (2024 / 2025 AD) ──────────────────────────────────────────
  { name: "Nepali New Year (Nawa Barsha)", bsYear: 2081, bsMonth: 1, bsDay: 1, description: "Official National Holiday - Bikram Sambat New Year" },
  { name: "International Workers' Day", bsYear: 2081, bsMonth: 1, bsDay: 19, description: "Majdur Diwas (May 1)", isRecurring: false },
  { name: "Buddha Jayanti / Ubhauli Parwa", bsYear: 2081, bsMonth: 2, bsDay: 10, description: "Birth anniversary of Gautam Buddha / Kirat festival" },
  { name: "Republic Day (Ganatantra Diwas)", bsYear: 2081, bsMonth: 2, bsDay: 15, description: "National Republic Day", isRecurring: false },
  { name: "Janai Purnima / Rakshya Bandhan", bsYear: 2081, bsMonth: 5, bsDay: 3, description: "Sacred thread festival / Raksha Bandhan" },
  { name: "Shree Krishna Janmashtami", bsYear: 2081, bsMonth: 5, bsDay: 10, description: "Birth anniversary of Lord Krishna" },
  { name: "Haritalika Teej", bsYear: 2081, bsMonth: 5, bsDay: 21, description: "Women's traditional fasting festival" },
  { name: "Indra Jatra", bsYear: 2081, bsMonth: 6, bsDay: 1, description: "Kathmandu Valley traditional rain and harvest festival" },
  { name: "Constitution Day (Sambidhan Diwas)", bsYear: 2081, bsMonth: 6, bsDay: 3, description: "National Constitution Day of Nepal" },
  { name: "Ghatasthapana", bsYear: 2081, bsMonth: 6, bsDay: 17, description: "First day of Bada Dashain" },
  { name: "Dashain - Fulpati", bsYear: 2081, bsMonth: 6, bsDay: 24, description: "Seventh day of Bada Dashain" },
  { name: "Dashain - Maha Ashtami", bsYear: 2081, bsMonth: 6, bsDay: 25, description: "Eighth day of Bada Dashain" },
  { name: "Dashain - Maha Nawami", bsYear: 2081, bsMonth: 6, bsDay: 26, description: "Ninth day of Bada Dashain" },
  { name: "Dashain - Vijaya Dashami", bsYear: 2081, bsMonth: 6, bsDay: 27, description: "Main day of Bada Dashain - Tika and Jamara" },
  { name: "Dashain - Ekadashi", bsYear: 2081, bsMonth: 6, bsDay: 28, description: "Eleventh day of Bada Dashain" },
  { name: "Dashain - Dwadashi", bsYear: 2081, bsMonth: 6, bsDay: 29, description: "Twelfth day of Bada Dashain" },
  { name: "Tihar - Laxmi Puja", bsYear: 2081, bsMonth: 7, bsDay: 15, description: "Festival of lights and worship of Goddess Laxmi" },
  { name: "Tihar - Gobardhan Puja / Mha Puja", bsYear: 2081, bsMonth: 7, bsDay: 17, description: "Newari New Year / Gobardhan Puja" },
  { name: "Tihar - Bhai Tika", bsYear: 2081, bsMonth: 7, bsDay: 18, description: "Main day of Tihar celebrating brother-sister bond" },
  { name: "Chhath Parwa", bsYear: 2081, bsMonth: 7, bsDay: 22, description: "Worship of the Sun God" },
  { name: "Christmas Day", bsYear: 2081, bsMonth: 9, bsDay: 10, description: "Christian festival celebrating birth of Jesus Christ" },
  { name: "Tamu Lhosar", bsYear: 2081, bsMonth: 9, bsDay: 15, description: "Gurung community New Year" },
  { name: "Maghe Sankranti", bsYear: 2081, bsMonth: 10, bsDay: 1, description: "Makar Sankranti / Winter solstice festival" },
  { name: "Sonam Lhosar", bsYear: 2081, bsMonth: 10, bsDay: 16, description: "Tamang community New Year" },
  { name: "Martyrs' Day (Shahid Diwas)", bsYear: 2081, bsMonth: 10, bsDay: 17, description: "Memorial for national martyrs of Nepal" },
  { name: "National Democracy Day (Prajatantra Diwas)", bsYear: 2081, bsMonth: 11, bsDay: 7, description: "Commemorating the 1951 democratic movement" },
  { name: "Maha Shivaratri", bsYear: 2081, bsMonth: 11, bsDay: 14, description: "Festival of Lord Shiva celebrated at Pashupatinath" },
  { name: "Gyalpo Lhosar", bsYear: 2081, bsMonth: 11, bsDay: 16, description: "Tibetan and Sherpa community New Year" },
  { name: "International Women's Day", bsYear: 2081, bsMonth: 11, bsDay: 24, description: "March 8 - Women's Day" },
  { name: "Fagu Purnima (Holi)", bsYear: 2081, bsMonth: 11, bsDay: 29, description: "Festival of Colors" },
  { name: "Ghode Jatra", bsYear: 2081, bsMonth: 12, bsDay: 16, description: "Horse festival celebrated in Tundikhel, Kathmandu" },

  // ── 2082 BS (2025 / 2026 AD) ──────────────────────────────────────────
  { name: "Nepali New Year (Nawa Barsha 2082)", bsYear: 2082, bsMonth: 1, bsDay: 1, description: "Official National Holiday - Bikram Sambat New Year" },
  { name: "International Workers' Day", bsYear: 2082, bsMonth: 1, bsDay: 18, description: "Majdur Diwas (May 1)" },
  { name: "Buddha Jayanti / Ubhauli Parwa", bsYear: 2082, bsMonth: 1, bsDay: 29, description: "Birth anniversary of Gautam Buddha / Kirat festival" },
  { name: "Republic Day (Ganatantra Diwas)", bsYear: 2082, bsMonth: 2, bsDay: 15, description: "National Republic Day (May 29)" },
  { name: "Janai Purnima / Rakshya Bandhan", bsYear: 2082, bsMonth: 4, bsDay: 24, description: "Sacred thread festival / Raksha Bandhan" },
  { name: "Shree Krishna Janmashtami", bsYear: 2082, bsMonth: 4, bsDay: 31, description: "Birth anniversary of Lord Krishna" },
  { name: "Haritalika Teej", bsYear: 2082, bsMonth: 5, bsDay: 10, description: "Women's traditional fasting festival" },
  { name: "Gaura Parwa", bsYear: 2082, bsMonth: 5, bsDay: 15, description: "Far-western regional cultural festival" },
  { name: "Indra Jatra", bsYear: 2082, bsMonth: 5, bsDay: 21, description: "Kathmandu Valley traditional rain and harvest festival" },
  { name: "Constitution Day (Sambidhan Diwas)", bsYear: 2082, bsMonth: 6, bsDay: 3, description: "National Constitution Day of Nepal (Sept 19)" },
  { name: "Ghatasthapana", bsYear: 2082, bsMonth: 6, bsDay: 6, description: "First day of Bada Dashain" },
  { name: "Dashain - Fulpati", bsYear: 2082, bsMonth: 6, bsDay: 13, description: "Seventh day of Bada Dashain" },
  { name: "Dashain - Maha Ashtami", bsYear: 2082, bsMonth: 6, bsDay: 14, description: "Eighth day of Bada Dashain" },
  { name: "Dashain - Maha Nawami", bsYear: 2082, bsMonth: 6, bsDay: 15, description: "Ninth day of Bada Dashain" },
  { name: "Dashain - Vijaya Dashami", bsYear: 2082, bsMonth: 6, bsDay: 16, description: "Main day of Bada Dashain - Tika and Jamara" },
  { name: "Dashain - Ekadashi", bsYear: 2082, bsMonth: 6, bsDay: 17, description: "Eleventh day of Bada Dashain" },
  { name: "Dashain - Dwadashi", bsYear: 2082, bsMonth: 6, bsDay: 18, description: "Twelfth day of Bada Dashain" },
  { name: "Tihar - Laxmi Puja", bsYear: 2082, bsMonth: 7, bsDay: 3, description: "Festival of lights and worship of Goddess Laxmi" },
  { name: "Tihar - Gobardhan Puja / Mha Puja", bsYear: 2082, bsMonth: 7, bsDay: 5, description: "Newari New Year / Gobardhan Puja" },
  { name: "Tihar - Bhai Tika", bsYear: 2082, bsMonth: 7, bsDay: 6, description: "Main day of Tihar celebrating brother-sister bond" },
  { name: "Chhath Parwa", bsYear: 2082, bsMonth: 7, bsDay: 10, description: "Worship of the Sun God" },
  { name: "Christmas Day", bsYear: 2082, bsMonth: 9, bsDay: 10, description: "Christian festival celebrating birth of Jesus Christ (Dec 25)" },
  { name: "Tamu Lhosar", bsYear: 2082, bsMonth: 9, bsDay: 15, description: "Gurung community New Year" },
  { name: "Maghe Sankranti", bsYear: 2082, bsMonth: 10, bsDay: 1, description: "Makar Sankranti / Winter solstice festival" },
  { name: "Martyrs' Day (Shahid Diwas)", bsYear: 2082, bsMonth: 10, bsDay: 16, description: "Memorial for national martyrs of Nepal" },
  { name: "Maha Shivaratri", bsYear: 2082, bsMonth: 11, bsDay: 3, description: "Festival of Lord Shiva celebrated at Pashupatinath" },
  { name: "Sonam Lhosar", bsYear: 2082, bsMonth: 11, bsDay: 5, description: "Tamang community New Year" },
  { name: "National Democracy Day (Prajatantra Diwas)", bsYear: 2082, bsMonth: 11, bsDay: 7, description: "Commemorating the 1951 democratic movement (Feb 19)" },
  { name: "Fagu Purnima (Holi)", bsYear: 2082, bsMonth: 11, bsDay: 18, description: "Festival of Colors" },
  { name: "International Women's Day", bsYear: 2082, bsMonth: 11, bsDay: 24, description: "March 8 - Women's Day" },
  { name: "Ghode Jatra", bsYear: 2082, bsMonth: 12, bsDay: 4, description: "Horse festival celebrated in Tundikhel, Kathmandu" },

  // ── 2083 BS (2026 / 2027 AD) ──────────────────────────────────────────
  { name: "Nepali New Year (Nawa Barsha 2083)", bsYear: 2083, bsMonth: 1, bsDay: 1, description: "Official National Holiday - Bikram Sambat New Year" },
  { name: "Buddha Jayanti / Ubhauli Parwa", bsYear: 2083, bsMonth: 1, bsDay: 17, description: "Birth anniversary of Gautam Buddha / Kirat festival" },
  { name: "International Workers' Day", bsYear: 2083, bsMonth: 1, bsDay: 18, description: "Majdur Diwas (May 1)" },
  { name: "Republic Day (Ganatantra Diwas)", bsYear: 2083, bsMonth: 2, bsDay: 15, description: "National Republic Day (May 29)" },
  { name: "Janai Purnima / Rakshya Bandhan", bsYear: 2083, bsMonth: 5, bsDay: 11, description: "Sacred thread festival / Raksha Bandhan" },
  { name: "Shree Krishna Janmashtami", bsYear: 2083, bsMonth: 5, bsDay: 18, description: "Birth anniversary of Lord Krishna" },
  { name: "Haritalika Teej", bsYear: 2083, bsMonth: 5, bsDay: 29, description: "Women's traditional fasting festival" },
  { name: "Constitution Day (Sambidhan Diwas)", bsYear: 2083, bsMonth: 6, bsDay: 3, description: "National Constitution Day of Nepal (Sept 19)" },
  { name: "Ghatasthapana", bsYear: 2083, bsMonth: 6, bsDay: 25, description: "First day of Bada Dashain" },
  { name: "Dashain - Fulpati", bsYear: 2083, bsMonth: 7, bsDay: 1, description: "Seventh day of Bada Dashain" },
  { name: "Dashain - Maha Ashtami", bsYear: 2083, bsMonth: 7, bsDay: 2, description: "Eighth day of Bada Dashain" },
  { name: "Dashain - Maha Nawami", bsYear: 2083, bsMonth: 7, bsDay: 3, description: "Ninth day of Bada Dashain" },
  { name: "Dashain - Vijaya Dashami", bsYear: 2083, bsMonth: 7, bsDay: 4, description: "Main day of Bada Dashain - Tika and Jamara" },
  { name: "Dashain - Ekadashi", bsYear: 2083, bsMonth: 7, bsDay: 5, description: "Eleventh day of Bada Dashain" },
  { name: "Dashain - Dwadashi", bsYear: 2083, bsMonth: 7, bsDay: 6, description: "Twelfth day of Bada Dashain" },
  { name: "Tihar - Laxmi Puja", bsYear: 2083, bsMonth: 7, bsDay: 23, description: "Festival of lights and worship of Goddess Laxmi" },
  { name: "Tihar - Gobardhan Puja / Mha Puja", bsYear: 2083, bsMonth: 7, bsDay: 25, description: "Newari New Year / Gobardhan Puja" },
  { name: "Tihar - Bhai Tika", bsYear: 2083, bsMonth: 7, bsDay: 26, description: "Main day of Tihar celebrating brother-sister bond" },
  { name: "Chhath Parwa", bsYear: 2083, bsMonth: 7, bsDay: 30, description: "Worship of the Sun God" },
  { name: "Christmas Day", bsYear: 2083, bsMonth: 9, bsDay: 10, description: "Christian festival celebrating birth of Jesus Christ (Dec 25)" },
  { name: "Tamu Lhosar", bsYear: 2083, bsMonth: 9, bsDay: 15, description: "Gurung community New Year" },
  { name: "Maghe Sankranti", bsYear: 2083, bsMonth: 10, bsDay: 1, description: "Makar Sankranti / Winter solstice festival" },
  { name: "Martyrs' Day (Shahid Diwas)", bsYear: 2083, bsMonth: 10, bsDay: 16, description: "Memorial for national martyrs of Nepal" },
  { name: "Sonam Lhosar", bsYear: 2083, bsMonth: 10, bsDay: 25, description: "Tamang community New Year" },
  { name: "National Democracy Day (Prajatantra Diwas)", bsYear: 2083, bsMonth: 11, bsDay: 7, description: "Commemorating the 1951 democratic movement (Feb 19)" },
  { name: "Maha Shivaratri", bsYear: 2083, bsMonth: 11, bsDay: 20, description: "Festival of Lord Shiva celebrated at Pashupatinath" },
  { name: "International Women's Day", bsYear: 2083, bsMonth: 11, bsDay: 24, description: "March 8 - Women's Day" },
  { name: "Fagu Purnima (Holi)", bsYear: 2083, bsMonth: 12, bsDay: 8, description: "Festival of Colors" },
  { name: "Ghode Jatra", bsYear: 2083, bsMonth: 12, bsDay: 23, description: "Horse festival celebrated in Tundikhel, Kathmandu" },
];

export function getResolvedNepalHolidays(): Array<{
  holidayDate: string; // YYYY-MM-DD (AD)
  holidayName: string;
  description: string;
  isRecurring: boolean;
  bsFormatted: string;
}> {
  return NEPAL_PUBLIC_HOLIDAYS_CATALOG.map((h) => {
    const ad = bsToAd(h.bsYear, h.bsMonth, h.bsDay);
    const dateStr = ad ? toAdString(ad) : "";
    const bsFormatted = `${h.bsYear}-${String(h.bsMonth).padStart(2, "0")}-${String(h.bsDay).padStart(2, "0")} BS`;
    return {
      holidayDate: dateStr,
      holidayName: h.name,
      description: h.description,
      isRecurring: h.isRecurring ?? false,
      bsFormatted,
    };
  }).filter((h) => Boolean(h.holidayDate));
}
