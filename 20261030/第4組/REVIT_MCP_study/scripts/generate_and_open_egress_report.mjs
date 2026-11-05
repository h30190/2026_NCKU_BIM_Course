import fs from 'fs';
import path from 'path';
import os from 'os';
import { execFile } from 'child_process';
import { buildEgressVisualReportHtml } from '../MCP-Server/build/tools/egress-audit-tools.js';

const fullData = {
  TotalRooms: 15,
  CompliantCount: 15,
  NonCompliantCount: 0,
  FailedCount: 0,
  MaxAllowedMeters: 50,
  OccupancyCategory: 'B-2 辦公場所（第 93 條第 3 款｜主要構造：防火構造）',
  LevelName: '01 - Entry Level',
  LevelElevationMeters: 0,
  ViewId: 312,
  ViewName: '01 - Entry Level',
  ScheduleId: 439884,
  ScheduleName: '避難步行距離檢討表',
  ScheduleFields: [
    '編號',
    '名稱',
    'Egress_Actual_Dist',
    'Egress_Max_Allowed',
    'Egress_Target_Exit',
    'Egress_Is_Compliant'
  ],
  SchedulePlacedOnSheet: false,
  ExitDoors: [
    { DoorId: 198054, Label: '120 (ID:198054)', AssignedRoomCount: 6 },
    { DoorId: 146741, Label: '130 (ID:146741)', AssignedRoomCount: 5 },
    { DoorId: 155278, Label: '132B (ID:155278)', AssignedRoomCount: 4 }
  ],
  MultiExitNearestSelection: true,
  Results: [
    {
      RoomId: 177334,
      RoomName: 'Conference 103',
      RoomNumber: '103',
      Success: true,
      PathOfTravelId: 439212,
      TagId: null,
      ActualDistanceMeters: 18.03,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [9.12, 0.29, 0.29, 2.46, 2.27, 0.26, 1.63, 0.36, 1.33],
      SegmentDimensionIds: [439217, 439220, 439223, 439226, 439229, 439232, 439235, 439238, 439241],
      IsCompliant: '合格',
      TargetExit: '132B (ID:155278)',
      TargetExitDoorId: 155278,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 18.03, CoveredVertices: 6, TotalVertices: 6, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 64.23, CoveredVertices: 6, TotalVertices: 6, IsNearest: false },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 105.78, CoveredVertices: 6, TotalVertices: 6, IsNearest: false }
      ]
    },
    {
      RoomId: 177333,
      RoomName: 'Instruction 104',
      RoomNumber: '104',
      Success: true,
      PathOfTravelId: 439252,
      TagId: null,
      ActualDistanceMeters: 19.65,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [9.75, 0.28, 6.03, 0.26, 1.63, 0.36, 1.33],
      SegmentDimensionIds: [439256, 439259, 439262, 439265, 439268, 439271, 439274],
      IsCompliant: '合格',
      TargetExit: '132B (ID:155278)',
      TargetExitDoorId: 155278,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 19.65, CoveredVertices: 4, TotalVertices: 4, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 58.29, CoveredVertices: 4, TotalVertices: 4, IsNearest: false },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 99.84, CoveredVertices: 4, TotalVertices: 4, IsNearest: false }
      ]
    },
    {
      RoomId: 177332,
      RoomName: 'Instruction 105',
      RoomNumber: '105',
      Success: true,
      PathOfTravelId: 439285,
      TagId: null,
      ActualDistanceMeters: 29.82,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [13.14, 1.02, 0.44, 11.64, 0.26, 1.63, 0.36, 1.33],
      SegmentDimensionIds: [439289, 439292, 439295, 439298, 439301, 439304, 439307, 439310],
      IsCompliant: '合格',
      TargetExit: '132B (ID:155278)',
      TargetExitDoorId: 155278,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 29.82, CoveredVertices: 4, TotalVertices: 4, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 50.51, CoveredVertices: 4, TotalVertices: 4, IsNearest: false },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 92.06, CoveredVertices: 4, TotalVertices: 4, IsNearest: false }
      ]
    },
    {
      RoomId: 177331,
      RoomName: 'Instruction 106',
      RoomNumber: '106',
      Success: true,
      PathOfTravelId: 439347,
      TagId: null,
      ActualDistanceMeters: 37.65,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [9.75, 0.28, 24.03, 0.26, 1.63, 0.36, 1.33],
      SegmentDimensionIds: [439351, 439354, 439357, 439360, 439363, 439366, 439369],
      IsCompliant: '合格',
      TargetExit: '132B (ID:155278)',
      TargetExitDoorId: 155278,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 37.65, CoveredVertices: 4, TotalVertices: 4, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 40.35, CoveredVertices: 4, TotalVertices: 4, IsNearest: false },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 81.9, CoveredVertices: 4, TotalVertices: 4, IsNearest: false }
      ]
    },
    {
      RoomId: 177330,
      RoomName: 'Instruction 108',
      RoomNumber: '108',
      Success: true,
      PathOfTravelId: 439382,
      TagId: null,
      ActualDistanceMeters: 33.01,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [12.11, 1.08, 6.47, 0.61, 0.33, 5.63, 0.88, 5.9],
      SegmentDimensionIds: [439417, 439420, 439423, 439426, 439429, 439432, 439435, 439438],
      IsCompliant: '合格',
      TargetExit: '120 (ID:198054)',
      TargetExitDoorId: 198054,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 33.01, CoveredVertices: 15, TotalVertices: 15, IsNearest: true },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 47.13, CoveredVertices: 15, TotalVertices: 15, IsNearest: false },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 74.56, CoveredVertices: 15, TotalVertices: 15, IsNearest: false }
      ]
    },
    {
      RoomId: 177325,
      RoomName: 'Lounge 111',
      RoomNumber: '111',
      Success: true,
      PathOfTravelId: 439443,
      TagId: null,
      ActualDistanceMeters: 23.75,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [8.27, 9.74, 5.74],
      SegmentDimensionIds: [439462, 439465, 439468],
      IsCompliant: '合格',
      TargetExit: '120 (ID:198054)',
      TargetExitDoorId: 198054,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 23.75, CoveredVertices: 7, TotalVertices: 8, IsNearest: true },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 62.1, CoveredVertices: 7, TotalVertices: 8, IsNearest: false },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 65, CoveredVertices: 7, TotalVertices: 8, IsNearest: false }
      ]
    },
    {
      RoomId: 177321,
      RoomName: 'Instruction 115',
      RoomNumber: '115',
      Success: true,
      PathOfTravelId: 439497,
      TagId: null,
      ActualDistanceMeters: 27.81,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [10.9, 2.28, 0.26, 14.36],
      SegmentDimensionIds: [439515, 439518, 439521, 439524],
      IsCompliant: '合格',
      TargetExit: '120 (ID:198054)',
      TargetExitDoorId: 198054,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 27.81, CoveredVertices: 6, TotalVertices: 7, IsNearest: true },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 66.04, CoveredVertices: 6, TotalVertices: 7, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 74.37, CoveredVertices: 6, TotalVertices: 7, IsNearest: false }
      ]
    },
    {
      RoomId: 177320,
      RoomName: 'Conference 116',
      RoomNumber: '116',
      Success: true,
      PathOfTravelId: 439526,
      TagId: null,
      ActualDistanceMeters: 21.25,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [8.31, 0.24, 2.56, 10.14],
      SegmentDimensionIds: [439539, 439542, 439545, 439548],
      IsCompliant: '合格',
      TargetExit: '120 (ID:198054)',
      TargetExitDoorId: 198054,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 21.25, CoveredVertices: 4, TotalVertices: 4, IsNearest: true },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 58.5, CoveredVertices: 4, TotalVertices: 4, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 69.93, CoveredVertices: 4, TotalVertices: 4, IsNearest: false }
      ]
    },
    {
      RoomId: 177318,
      RoomName: 'Instruction 117',
      RoomNumber: '117',
      Success: true,
      PathOfTravelId: 439552,
      TagId: null,
      ActualDistanceMeters: 20.88,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [9.28, 8.41, 3.18],
      SegmentDimensionIds: [439569, 439572, 439575],
      IsCompliant: '合格',
      TargetExit: '120 (ID:198054)',
      TargetExitDoorId: 198054,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 20.88, CoveredVertices: 6, TotalVertices: 6, IsNearest: true },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 55.52, CoveredVertices: 6, TotalVertices: 6, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 72.73, CoveredVertices: 6, TotalVertices: 6, IsNearest: false }
      ]
    },
    {
      RoomId: 177319,
      RoomName: 'Lounge 120',
      RoomNumber: '120',
      Success: true,
      PathOfTravelId: 439604,
      TagId: null,
      ActualDistanceMeters: 6.76,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [1.06, 5.7],
      SegmentDimensionIds: [439622, 439625],
      IsCompliant: '合格',
      TargetExit: '120 (ID:198054)',
      TargetExitDoorId: 198054,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 6.76, CoveredVertices: 6, TotalVertices: 6, IsNearest: true },
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 52.42, CoveredVertices: 6, TotalVertices: 6, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 66.18, CoveredVertices: 6, TotalVertices: 6, IsNearest: false }
      ]
    },
    {
      RoomId: 177305,
      RoomName: 'Cafeteria 121',
      RoomNumber: '121',
      Success: true,
      PathOfTravelId: 439638,
      TagId: null,
      ActualDistanceMeters: 38.02,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [10.3, 3.05, 4.46, 16.46, 0.31, 1.61, 0.26, 1.57],
      SegmentDimensionIds: [439658, 439661, 439664, 439667, 439670, 439673, 439676, 439679],
      IsCompliant: '合格',
      TargetExit: '130 (ID:146741)',
      TargetExitDoorId: 146741,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 38.02, CoveredVertices: 10, TotalVertices: 11, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 39.7, CoveredVertices: 10, TotalVertices: 11, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 97.69, CoveredVertices: 10, TotalVertices: 11, IsNearest: false }
      ]
    },
    {
      RoomId: 177306,
      RoomName: 'Prep/Dish 122',
      RoomNumber: '122',
      Success: true,
      PathOfTravelId: 439687,
      TagId: null,
      ActualDistanceMeters: 40.27,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [5.87, 0.28, 0.28, 8.53, 0.31, 0.31, 4.49, 16.46, 0.31, 1.61, 0.26, 1.57],
      SegmentDimensionIds: [439694, 439697, 439700, 439703, 439706, 439709, 439712, 439715, 439718, 439721, 439724, 439727],
      IsCompliant: '合格',
      TargetExit: '130 (ID:146741)',
      TargetExitDoorId: 146741,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 40.27, CoveredVertices: 4, TotalVertices: 4, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 42.79, CoveredVertices: 4, TotalVertices: 4, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 100.84, CoveredVertices: 4, TotalVertices: 4, IsNearest: false }
      ]
    },
    {
      RoomId: 177309,
      RoomName: 'Conference 123',
      RoomNumber: '123',
      Success: true,
      PathOfTravelId: 439762,
      TagId: null,
      ActualDistanceMeters: 20.86,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [9.3, 0.3, 7.51, 0.31, 1.61, 0.26, 1.57],
      SegmentDimensionIds: [439777, 439780, 439783, 439786, 439789, 439792, 439795],
      IsCompliant: '合格',
      TargetExit: '130 (ID:146741)',
      TargetExitDoorId: 146741,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 20.86, CoveredVertices: 7, TotalVertices: 7, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 43.2, CoveredVertices: 7, TotalVertices: 7, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 101.31, CoveredVertices: 7, TotalVertices: 7, IsNearest: false }
      ]
    },
    {
      RoomId: 177311,
      RoomName: 'Admin 126',
      RoomNumber: '126',
      Success: true,
      PathOfTravelId: 439805,
      TagId: null,
      ActualDistanceMeters: 13.54,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [6.01, 0.32, 3.46, 0.31, 1.61, 0.26, 1.57],
      SegmentDimensionIds: [439813, 439816, 439819, 439822, 439825, 439828, 439831],
      IsCompliant: '合格',
      TargetExit: '130 (ID:146741)',
      TargetExitDoorId: 146741,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 13.54, CoveredVertices: 5, TotalVertices: 5, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 45.17, CoveredVertices: 5, TotalVertices: 5, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 103.28, CoveredVertices: 5, TotalVertices: 5, IsNearest: false }
      ]
    },
    {
      RoomId: 177310,
      RoomName: 'Office 127',
      RoomNumber: '127',
      Success: true,
      PathOfTravelId: 439837,
      TagId: null,
      ActualDistanceMeters: 17.45,
      MaxAllowedMeters: 50,
      SegmentLengthsMeters: [3.83, 0.27, 5.66, 0.31, 0.29, 3.33, 0.31, 1.61, 0.26, 1.57],
      SegmentDimensionIds: [439849, 439852, 439855, 439858, 439861, 439864, 439867, 439870, 439873, 439876],
      IsCompliant: '合格',
      TargetExit: '130 (ID:146741)',
      TargetExitDoorId: 146741,
      NearestExitAutoSelected: true,
      CandidateExits: [
        { DoorId: 146741, Label: '130 (ID:146741)', FarthestDistanceMeters: 17.45, CoveredVertices: 5, TotalVertices: 5, IsNearest: true },
        { DoorId: 198054, Label: '120 (ID:198054)', FarthestDistanceMeters: 49.32, CoveredVertices: 5, TotalVertices: 5, IsNearest: false },
        { DoorId: 155278, Label: '132B (ID:155278)', FarthestDistanceMeters: 107.43, CoveredVertices: 5, TotalVertices: 5, IsNearest: false }
      ]
    }
  ]
};

const port = 8964;
const html = buildEgressVisualReportHtml(fullData, port);
const tempPath = path.join(os.tmpdir(), 'RevitMCP_EgressReport.html');
fs.writeFileSync(tempPath, html, 'utf-8');
console.log('Written to ' + tempPath);

// 透過 Microsoft Edge 開啟
execFile('cmd', ['/c', 'start', 'msedge', tempPath], { windowsHide: true }, (err) => {
  if (err) console.error('Error opening Edge:', err);
  else console.log('Successfully launched Edge!');
});

