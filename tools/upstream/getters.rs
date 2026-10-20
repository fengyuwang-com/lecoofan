use super::EcDevice;
use crate::ec::{REG_CHIP_ID1, REG_CHIP_ID2, REG_CHIP_VER};
use anyhow::Result;
use lecoo_types::{caps::SensorRole, ec_types::FanIndex};

pub fn read_system_info(ec: &EcDevice) -> Result<(u8, u8, u8)> {
    ec.with_batch(|b| {
        let chip_id1 = b.read_abs(REG_CHIP_ID1)?;
        let chip_id2 = b.read_abs(REG_CHIP_ID2)?;
        let chip_ver = b.read_abs(REG_CHIP_VER)?;
        Ok((chip_id1, chip_id2, chip_ver))
    })
}

/// Missing fans report 0 so the existing two-slot IPC response stays valid.
pub fn read_fans_rpm(ec: &EcDevice) -> Result<(u16, u16)> {
    ec.with_batch(|b| {
        let read_one = |index: FanIndex| -> Result<u16> {
            let Some(spec) = ec.profile.fan(index) else {
                return Ok(0);
            };
            let msb = b.read(spec.rpm_msb)? as u16;
            let lsb = b.read(spec.rpm_lsb)? as u16;
            Ok((msb << 8) | lsb)
        };

        Ok((read_one(FanIndex::Cpu)?, read_one(FanIndex::Gpu)?))
    })
}

/// Reads all fan RPM values from the EC. Currently not used.
// pub fn read_fans_all(ec: &EcDevice) -> Result<Vec<(FanIndex, u16)>> {
//     ec.with_batch(|b| {
//         let mut out = Vec::with_capacity(ec.profile.fans.len());
//         for f in ec.profile.fans {
//             let msb = b.read(f.rpm_msb)? as u16;
//             let lsb = b.read(f.rpm_lsb)? as u16;
//             out.push((f.index, (msb << 8) | lsb));
//         }
//         Ok(out)
//     })
// }

pub fn read_temperatures(ec: &EcDevice) -> Result<(u8, u8)> {
    ec.with_batch(|b| {
        let read_one = |role: SensorRole| -> Result<u8> {
            match ec.profile.sensor(role) {
                Some(spec) => b.read(spec.addr),
                None => Ok(0),
            }
        };
        Ok((read_one(SensorRole::Cpu)?, read_one(SensorRole::Sys)?))
    })
}
